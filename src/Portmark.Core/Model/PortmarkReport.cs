using System.Text.Json.Serialization;

namespace Portmark.Core.Model;

/// <summary>
/// The whole answer, in the shape other tools will consume.
///
/// The governing rule of this schema: a field is null when the hardware did not report it, and
/// there is always an accompanying reason saying why. Nothing is ever inferred from the presence
/// or shape of a connector. A USB-C socket tells you the shape of the socket and nothing else.
/// </summary>
public sealed class PortmarkReport
{
    public string Tool { get; init; } = "portmark";
    public string SchemaVersion { get; init; } = "1";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public MachineReport Machine { get; init; } = new();
    public CapabilityReport Capability { get; init; } = new();
    public List<ConnectorReport> Connectors { get; init; } = [];
}

public sealed class MachineReport
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? BiosVersion { get; init; }
    public string? OsVersion { get; init; }
    public string? OsDisplayVersion { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapabilityStatus
{
    /// <summary>Cable and connector data can be read right now.</summary>
    Ok,

    /// <summary>The hardware can do this, but a one-time administrator step is needed first.</summary>
    NeedsSetup,

    /// <summary>This machine cannot report cable data at all.</summary>
    Unsupported,
}

/// <summary>
/// Whether this machine can answer the question, stated before any answer is given. Roughly a
/// third of machines cannot, and saying so plainly is better than showing a blank panel.
/// </summary>
public sealed class CapabilityReport
{
    public CapabilityStatus Status { get; set; } = CapabilityStatus.Unsupported;

    /// <summary>Plain English, suitable for showing directly to a user.</summary>
    public string Explanation { get; set; } = "";

    /// <summary>What to do about it, when there is something to do.</summary>
    public string? Remedy { get; set; }

    public bool UcmDevicePresent { get; set; }
    public string? UcmDeviceInstanceId { get; set; }
    public bool TestInterfaceEnabled { get; set; }
    public bool TestInterfacePublished { get; set; }
    public string? UcsiVersion { get; set; }
    public int? ConnectorCount { get; set; }

    /// <summary>
    /// What the port controller advertises in GET_CAPABILITY. Decisive for honesty: a controller
    /// that does not advertise CableDetailsAvailable will never report cable data, and saying
    /// "this cable has no e-marker" in that case would be a fabrication.
    /// </summary>
    public PpmFeatureReport? Features { get; set; }
}

public sealed class PpmFeatureReport
{
    public bool CableDetailsAvailable { get; init; }
    public bool AlternateModeDetailsAvailable { get; init; }
    public bool PowerDataObjectDetailsAvailable { get; init; }
    public bool SupportsUsbPowerDelivery { get; init; }
    public bool SupportsBatteryCharging { get; init; }
    public int AlternateModeCount { get; init; }
    public string? PowerDeliveryVersion { get; init; }
    public string? TypeCVersion { get; init; }
    public string? BatteryChargingVersion { get; init; }
    public string OptionalFeaturesHex { get; init; } = "";
}

public sealed class ConnectorReport
{
    public int Index { get; init; }

    /// <summary>Null when the connector status could not be read at all.</summary>
    public bool? Connected { get; set; }

    public string? PartnerType { get; set; }
    public string? PowerOperationMode { get; set; }
    public string? PowerDirection { get; set; }
    public CableReport Cable { get; set; } = new();
    public RawReport Raw { get; set; } = new();

    /// <summary>The one-line plain English rendering.</summary>
    public string Summary { get; set; } = "";
}

/// <summary>
/// What the cable's e-marker reported. Every value is nullable, and <see cref="DataAvailable"/>
/// being false is a real, informative answer rather than an error.
/// </summary>
public sealed class CableReport
{
    public bool DataAvailable { get; set; }

    /// <summary>Why there is no data, when there is none.</summary>
    public string? Reason { get; set; }

    public SpeedReport? Speed { get; set; }
    public int? CurrentCapabilityMilliamps { get; set; }
    public int? MaxWattsAt20Volts { get; set; }
    public string? PlugType { get; set; }
    public bool? ActiveCable { get; set; }
    public bool? VbusInCable { get; set; }
    public bool? SupportsAlternateModes { get; set; }
    public int? LatencyCode { get; set; }

    /// <summary>
    /// Always null for now, and deliberately so. UCSI GET_CABLE_PROPERTY carries no video
    /// capability field, so claiming one either way would be a guess.
    /// </summary>
    public bool? SupportsVideo { get; set; }

    public string? VideoNote { get; set; }
}

public sealed class SpeedReport
{
    public int Mantissa { get; init; }
    public string Unit { get; init; } = "";
    public string Display { get; init; } = "";

    /// <summary>Normalised to bits per second so consumers can compare without parsing.</summary>
    public long? BitsPerSecond { get; init; }
}

/// <summary>The undecoded bytes, so anyone can check the decoding or decode it differently.</summary>
public sealed class RawReport
{
    public string? ConnectorStatusHex { get; set; }
    public string? CablePropertyHex { get; set; }
    public string? ConnectorStatusCci { get; set; }
    public string? CablePropertyCci { get; set; }
}
