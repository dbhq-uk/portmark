using System.Text.Json;
using Portmark.Core.Model;
using Portmark.Core.Usb;
using Xunit;

namespace Portmark.Core.Tests;

/// <summary>
/// The vendor name table is the USB ID Repository's data, not the hardware's. These tests check
/// that it is read correctly, that an ID it does not list comes back null rather than as a near
/// miss, and that a standard SVID is never given a vendor's name.
///
/// The expected names are checked against usb.ids 2026.06.26 as published, not against the
/// generated table, so a generator that mangled names would fail here.
/// </summary>
public class VendorNamesTests
{
    [Theory]
    [InlineData(0x17EF, "Lenovo")]
    [InlineData(0x05AC, "Apple, Inc.")]
    [InlineData(0x8087, "Intel Corp.")]
    public void KnownVendorIdResolvesToItsRegisteredName(int vendorId, string expected)
    {
        Assert.Equal(expected, VendorNames.Find((ushort)vendorId));
    }

    [Fact]
    public void UnlistedVendorIdIsNull()
    {
        // 0x0000 is not assigned to anyone, so the table has no entry. Null, not an empty string
        // and not the nearest neighbour.
        Assert.Null(VendorNames.Find(0x0000));
    }

    [Theory]
    [InlineData("0x05AC", "Apple, Inc.")]
    [InlineData("0x05ac", "Apple, Inc.")]
    [InlineData("0x0000", null)]
    [InlineData("05AC", null)]
    [InlineData("0x5AC", null)]
    [InlineData("0xZZZZ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void VendorIdStringsAreOnlyAcceptedInTheReportsOwnFormat(string? vendorId, string? expected)
    {
        // The reports hold IDs as "0x" and four hex digits. Anything else is not a vendor ID the
        // report produced, so it gets no name rather than a guessed parse.
        Assert.Equal(expected, VendorNames.Find(vendorId));
    }

    [Fact]
    public void EmbeddedTableLoadsInFull()
    {
        // usb.ids 2026.06.26 lists 3427 vendors. A resource that failed to embed, or a parser
        // that stopped early, would show up here rather than as names quietly going missing.
        Assert.Equal(3427, VendorNames.Count);
    }

    [Fact]
    public void ConcurrentFirstUseSeesOneCompleteTable()
    {
        string?[] names = new string?[64];
        Parallel.For(0, names.Length, i => names[i] = VendorNames.Find(0x17EF));

        Assert.All(names, n => Assert.Equal("Lenovo", n));
    }

    [Fact]
    public void ParserReadsTabSeparatedLinesAndSkipsCommentsBlanksAndMalformedLines()
    {
        const string table =
            "# a header comment\r\n"
          + "05AC\tApple, Inc.\r\n"
          + "\r\n"
          + "17ef\tLenovo\n"
          + "not a vendor line\n"
          + "12345\ttoo many digits\n"
          + "0BDA\t\n"
          + "8087\tIntel Corp.\tstray field kept as part of the name\n";

        IReadOnlyDictionary<ushort, string> parsed = VendorNames.Parse(new StringReader(table));

        Assert.Equal(3, parsed.Count);
        Assert.Equal("Apple, Inc.", parsed[0x05AC]);
        Assert.Equal("Lenovo", parsed[0x17EF]);
        Assert.Equal("Intel Corp.\tstray field kept as part of the name", parsed[0x8087]);
        Assert.False(parsed.ContainsKey(0x0BDA));
    }

    [Fact]
    public void ParserKeepsTheFirstNameWhenAnIdRepeats()
    {
        IReadOnlyDictionary<ushort, string> parsed =
            VendorNames.Parse(new StringReader("05AC\tFirst\n05AC\tSecond\n"));

        Assert.Equal("First", parsed[0x05AC]);
    }

    [Theory]
    [InlineData("0xFF01")]   // DisplayPort, assigned by VESA
    [InlineData("0xFF00")]   // the USB-IF's own SID, which usb.ids does list as a vendor line
    public void StandardSvidsAreNeverGivenAVendorName(string svid)
    {
        Assert.Null(VendorNames.ForSvid(svid));
    }

    [Fact]
    public void VendorSvidIsNamedByTheVendorIdItIs()
    {
        Assert.Equal("Intel Corp.", VendorNames.ForSvid("0x8087"));
    }

    [Fact]
    public void ReportsCarryTheRegisteredNameBesideTheVendorId()
    {
        var device = new UsbDeviceReport { VendorId = "0x05AC", ProductId = "0x12A8" };
        var billboard = new BillboardReport { VendorId = "0x17EF", ProductId = "0x0001" };
        var mode = new AlternateModeReport { Svid = "0x8087" };
        var ucsiMode = new PortAlternateModeReport { Svid = "0xFF01" };

        Assert.Equal("Apple, Inc.", device.VendorName);
        Assert.Equal("Lenovo", billboard.VendorName);
        Assert.Equal("Intel Corp.", mode.VendorName);
        Assert.Null(ucsiMode.VendorName);

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(device, options));
        Assert.Equal("Apple, Inc.", json.RootElement.GetProperty("vendorName").GetString());
    }
}
