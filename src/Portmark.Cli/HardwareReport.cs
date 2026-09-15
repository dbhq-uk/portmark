using System.Management;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Portmark.Core;
using Portmark.Core.Model;
using Portmark.Core.Reporting;

namespace Portmark.Cli;

/// <summary>
/// <c>portmark report</c>: one normal read, written to a single file that can be attached to a
/// hardware report issue.
///
/// Whether a port controller reports cable details is undocumented and can only be collected, and
/// pasted <c>--human</c> output loses the raw bytes that would let anyone check a decode. The file
/// carries both, plus enough about the machine to group reports by firmware. It is written locally
/// and nothing more: no browser is opened and no network call is made, because the README promises
/// portmark never makes one.
/// </summary>
internal static class HardwareReport
{
    /// <summary>
    /// The shape of the report file. Independent of the reading's own <c>schemaVersion</c>, which
    /// travels inside the file unchanged.
    /// </summary>
    public const string SchemaVersion = "1";

    public const string IssueTemplateUrl = "https://github.com/dbhq-uk/portmark/issues/new?template=hardware-report.yml";

    /// <summary>
    /// Nulls are kept in the envelope so a value that could not be read shows as null rather than
    /// vanishing. Relaxed escaping because this file is read by people checking what they are about
    /// to share, and "VID_0BDA&PID_8153" gets in the way of that.
    /// </summary>
    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string[] args)
    {
        string? requested = null;
        int outIndex = Array.IndexOf(args, "--out");
        if (outIndex >= 0)
        {
            if (outIndex + 1 >= args.Length || args[outIndex + 1].StartsWith('-'))
                return Program.Fail("--out needs a file path.");
            requested = args[outIndex + 1];
        }

        bool redact = !args.Contains("--no-redact");
        string path = Path.GetFullPath(requested ?? $"portmark-report-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        PortmarkReport reading = PortmarkReader.Read();

        var human = new StringWriter();
        Program.WriteHuman(reading, human);

        var root = new JsonObject
        {
            ["tool"] = "portmark",
            ["reportSchemaVersion"] = SchemaVersion,
            ["createdAt"] = DateTimeOffset.UtcNow,
            ["environment"] = JsonSerializer.SerializeToNode(ReadEnvironment(reading), FileOptions),
            ["redaction"] = null,   // filled in below, once there is something to say; kept here for its position
            ["human"] = new JsonArray(human.ToString().TrimEnd()
                                           .Split(Environment.NewLine)
                                           .Select(line => (JsonNode?)line)
                                           .ToArray()),
            // The same JSON `portmark` prints, so anything that reads one reads the other.
            ["reading"] = JsonSerializer.SerializeToNode(reading, Program.JsonOptions),
        };

        var redactor = new ReportRedactor(IdentityHints.FromEnvironment());
        if (redact) redactor.Redact(root);

        root["redaction"] = new JsonObject
        {
            ["applied"] = redact,
            ["note"] = redact
                ? "Values that could identify you or this PC were replaced with placeholders. Each value has "
                + "one placeholder throughout this file, so different devices stay distinguishable. The "
                + "original values are not stored anywhere in it. 'portmark report --no-redact' keeps them."
                : "Nothing was redacted: this file was written with --no-redact, and may contain serial "
                + "numbers, device paths, this PC's name and your user name.",
            ["looksFor"] = JsonSerializer.SerializeToNode(ReportRedactor.LooksFor, FileOptions),
            ["redacted"] = JsonSerializer.SerializeToNode(redactor.Entries, FileOptions),
        };

        try
        {
            File.WriteAllText(path, root.ToJsonString(FileOptions) + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Program.Fail($"could not write the report to {path}: {e.Message}");
        }

        string redaction = !redact
            ? "NOT redacted (--no-redact), so check it before sharing"
            : redactor.Entries.Count == 0
                ? "nothing identifying was found to redact"
                : $"{redactor.Entries.Count} identifying value(s) replaced with placeholders, listed in the file";

        Console.WriteLine($"Wrote {path}");
        Console.WriteLine($"It holds one reading ({reading.Capability.Status}, {reading.Connectors.Count} connector(s)) "
                        + $"as JSON with raw bytes and as --human text, plus this PC's make, model, BIOS and "
                        + $"Windows version; {redaction}.");
        Console.WriteLine();
        Console.WriteLine("Nothing was sent anywhere. To contribute it, attach the file to a hardware report:");
        Console.WriteLine($"  {IssueTemplateUrl}");

        return Program.ExitCodeFor(reading);
    }

    private static ReportEnvironment ReadEnvironment(PortmarkReport reading)
    {
        using RegistryKey? bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using RegistryKey? cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

        string? build = cv?.GetValue("CurrentBuildNumber")?.ToString();
        object? ubr = cv?.GetValue("UBR");
        CapabilityReport capability = reading.Capability;

        return new ReportEnvironment
        {
            PortmarkVersion = Program.Version,
            OsCaption = ReadOsCaption(),
            OsVersion = reading.Machine.OsVersion,
            OsBuild = build is null ? null : ubr is null ? build : $"{build}.{ubr}",
            OsDisplayVersion = reading.Machine.OsDisplayVersion,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            MachineManufacturer = reading.Machine.Manufacturer,
            MachineModel = reading.Machine.Model,
            BiosVendor = bios?.GetValue("BIOSVendor")?.ToString(),
            BiosVersion = reading.Machine.BiosVersion,
            UcsiVersion = capability.UcsiVersion,
            UcsiOptionalFeaturesHex = capability.Features?.OptionalFeaturesHex,
            UcsiFeatures = capability.Features,
            UcsiNote = capability.Features is null
                ? $"Not read. The port controller did not get as far as reporting its version and features "
                + $"(status {capability.Status}); the reading's capability section says why."
                : null,
        };
    }

    /// <summary>
    /// The OS name as Windows presents it, from local WMI. The registry's ProductName is not used
    /// because it still says "Windows 10" on Windows 11. Null when WMI will not answer, rather than
    /// a name pieced together from build numbers.
    /// </summary>
    private static string? ReadOsCaption()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject os in results)
                using (os) return os["Caption"]?.ToString()?.Trim();
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}

/// <summary>What a hardware report needs to know about the machine, to group reports by firmware.</summary>
internal sealed class ReportEnvironment
{
    public string PortmarkVersion { get; init; } = "";
    public string? OsCaption { get; init; }
    public string? OsVersion { get; init; }
    public string? OsBuild { get; init; }
    public string? OsDisplayVersion { get; init; }
    public string OsArchitecture { get; init; } = "";
    public string? MachineManufacturer { get; init; }
    public string? MachineModel { get; init; }
    public string? BiosVendor { get; init; }
    public string? BiosVersion { get; init; }
    public string? UcsiVersion { get; init; }
    public string? UcsiOptionalFeaturesHex { get; init; }
    public PpmFeatureReport? UcsiFeatures { get; init; }
    public string? UcsiNote { get; init; }
}
