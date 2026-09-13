using System.Text.Json.Serialization;

namespace UcsiProbe;

/// <summary>One attempted API call, recorded whether it succeeded or failed.</summary>
public sealed record Attempt
{
    public required string Stage { get; init; }
    public required string Api { get; init; }
    public required string Target { get; init; }
    public string? Detail { get; init; }
    public required bool Success { get; init; }
    public int Win32Error { get; init; }
    public string? Error { get; init; }
    public string? Result { get; init; }
    public string? DataHex { get; init; }
}

/// <summary>Which of the four spike outcomes the machine supports.</summary>
public enum Verdict
{
    /// <summary>Readable from user mode, no admin, no registry change.</summary>
    A_UserModeNoChanges,
    /// <summary>Readable from user mode after a one-time elevated TestInterfaceEnabled registry change.</summary>
    B_UserModeAfterRegistryFlag,
    /// <summary>Readable only by shelling out to UcsiControl.exe from the MUTT package.</summary>
    C_UcsiControlOnly,
    /// <summary>Not readable at all on this hardware.</summary>
    D_NotReadable,
    /// <summary>Stage A found nothing and the registry flag was never set, so B and D are not yet separable.</summary>
    Undetermined_StageBNotExercised,
}

public sealed class Report
{
    public string Tool { get; set; } = "ucsiprobe";
    public string SchemaVersion { get; set; } = "1";
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public Machine Machine { get; set; } = new();
    public bool Elevated { get; set; }
    public bool TestInterfaceEnabledFlagSet { get; set; }
    public string? TestInterfaceFlagKey { get; set; }
    public List<UcmDevice> UcmDevices { get; set; } = [];
    public List<Attempt> Attempts { get; set; } = [];
    public List<string> CablePropertyBytes { get; set; } = [];

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Verdict Verdict { get; set; } = Verdict.D_NotReadable;
    public string VerdictReason { get; set; } = "";
    public List<string> Notes { get; set; } = [];

    public void Add(Attempt a) => Attempts.Add(a);
}

public sealed class Machine
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Sku { get; set; }
    public string? BiosVersion { get; set; }
    public string? OsVersion { get; set; }
    public string? OsDisplayVersion { get; set; }
    public string? Architecture { get; set; }
}

public sealed class UcmDevice
{
    public string? InstanceId { get; set; }
    public string? Description { get; set; }
    public string? Service { get; set; }
    public string? DriverVersion { get; set; }
    public List<string> InterfaceClasses { get; set; } = [];
    public List<string> InterfacePaths { get; set; } = [];
    public bool TestInterfaceEnabled { get; set; }
}
