using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;

namespace Portmark.Core.Usb;

/// <summary>
/// The name registered to a USB vendor ID, from a copy of the USB ID Repository's usb.ids embedded
/// at build time. Looked up offline: nothing is fetched and nothing is sent anywhere.
///
/// A registered name says who holds the number, and nothing more. It is not proof of who made a
/// device: products routinely ship under the vendor ID of the chip inside them, and a device can
/// report any ID it likes. So the name is always shown as the ID's registered name, never as the
/// maker, and the device's own manufacturer string stays a separate field with its own meaning.
///
/// The table is regenerated with scripts/update-usb-vendors.ps1. It is someone else's list and
/// can lag behind new assignments, so an ID it does not list is null, not "unknown vendor".
/// </summary>
public static class VendorNames
{
    private const string ResourceName = "Portmark.Core.Usb.usb-vendors.tsv";

    /// <summary>
    /// SVIDs from here up are Standard IDs rather than vendor IDs: 0xFF00 is the USB-IF's own and
    /// 0xFF01 is DisplayPort, assigned by VESA. usb.ids happens to list 0xFF00 as a vendor line,
    /// which is exactly the confusion this bound exists to avoid.
    /// </summary>
    private const ushort FirstStandardSvid = 0xFF00;

    // Lazy's default mode is ExecutionAndPublication: the table is parsed once, by whichever thread
    // asks first, and every other thread waits for that one result rather than parsing its own.
    private static readonly Lazy<FrozenDictionary<ushort, string>> Table = new(Load);

    /// <summary>How many vendor IDs the embedded table names.</summary>
    public static int Count => Table.Value.Count;

    /// <summary>The name registered to this vendor ID, or null when the table lists none.</summary>
    public static string? Find(ushort vendorId) => Table.Value.GetValueOrDefault(vendorId);

    /// <summary>
    /// The same lookup for an ID as the reports hold it, "0x" and four hex digits. Anything else
    /// is null: a string in some other shape did not come from a descriptor, and parsing it
    /// generously would put a name on something the hardware never said.
    /// </summary>
    public static string? Find(string? vendorId) => TryParseId(vendorId, out ushort id) ? Find(id) : null;

    /// <summary>
    /// The registered name for an Alternate Mode SVID. Below 0xFF00 an SVID is the vendor's USB
    /// vendor ID, so it has one. From 0xFF00 up it is a Standard ID with a name of its own, already
    /// given by <see cref="BillboardReader.SvidName"/>, and this returns null.
    /// </summary>
    public static string? ForSvid(string? svid)
        => TryParseId(svid, out ushort id) && id < FirstStandardSvid ? Find(id) : null;

    /// <summary>
    /// Reads the table format: one vendor per line, four hex digits, a tab, then the name. Lines
    /// starting with # are comments. A line in any other shape is skipped, and the first name for
    /// a repeated ID is kept; the generator refuses to produce either, so neither should occur.
    /// </summary>
    public static IReadOnlyDictionary<ushort, string> Parse(TextReader reader)
    {
        var table = new Dictionary<ushort, string>();

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.Length < 6 || line[4] != '\t') continue;
            if (!ushort.TryParse(line.AsSpan(0, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort id))
                continue;

            string name = line[5..];
            if (string.IsNullOrWhiteSpace(name)) continue;

            table.TryAdd(id, name);
        }

        return table;
    }

    private static FrozenDictionary<ushort, string> Load()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);

        // A missing resource is a build fault, and the test suite checks the table's size to catch
        // it. At run time it only costs the names: every lookup is null, which is the honest answer
        // when the list is not there, rather than a crash in the middle of reading the ports.
        if (stream is null) return FrozenDictionary<ushort, string>.Empty;

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return Parse(reader).ToFrozenDictionary();
    }

    private static bool TryParseId(string? value, out ushort id)
    {
        id = 0;
        return value is { Length: 6 }
            && value.StartsWith("0x", StringComparison.Ordinal)
            && ushort.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
    }
}
