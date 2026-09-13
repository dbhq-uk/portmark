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

    /// <summary>
    /// USB Billboard devices found on any port. Read through public USB hub IOCTLs, so this is
    /// populated even when the UCSI path is unavailable and even with no administrator rights.
    /// </summary>
    public List<BillboardReport> Billboards { get; set; } = [];
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
    /// <summary>What the connector itself supports, as distinct from what is attached to it.</summary>
    public ConnectorCapabilityReport? Capability { get; set; }

    /// <summary>
    /// Which alternate mode index is currently active, when the controller reports one. Knowing a
    /// mode is active is not the same as knowing which: identifying it needs GET_ALTERNATE_MODES,
    /// which some controllers advertise but decline.
    /// </summary>
    public int? ActiveAlternateModeIndex { get; set; }
    public int? SupportedAlternateModeBitmap { get; set; }
    public string? AlternateModeNote { get; set; }

    public CableReport Cable { get; set; } = new();

    /// <summary>
    /// What the attached supply offers and what was actually negotiated. Available on controllers
    /// that advertise PDO details, independently of whether cable details are available.
    /// </summary>
    public PowerReport Power { get; set; } = new();

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

public sealed class ConnectorCapabilityReport
{
    public bool SupportsUsb2 { get; init; }
    public bool SupportsUsb3 { get; init; }
    public bool SupportsAlternateModes { get; init; }
    public bool SupportsDualRolePower { get; init; }
    public bool SupportsAudioAccessory { get; init; }
    public bool SupportsDebugAccessory { get; init; }
    public bool CanProvidePower { get; init; }
    public bool CanConsumePower { get; init; }
    public string Raw { get; init; } = "";
}

public sealed class PowerReport
{
    public bool DataAvailable { get; set; }
    public string? Reason { get; set; }

    /// <summary>What the attached supply advertises it can provide.</summary>
    public List<PowerObjectReport> PartnerSource { get; set; } = [];

    /// <summary>What this PC advertises it can provide.</summary>
    public List<PowerObjectReport> LocalSource { get; set; } = [];

    /// <summary>The contract actually in force, when one could be decoded.</summary>
    public RequestReport? Negotiated { get; set; }

    /// <summary>Highest power the attached supply offers, in milliwatts.</summary>
    public int? MaxAvailableMilliwatts { get; set; }
}

public sealed class PowerObjectReport
{
    public string Kind { get; init; } = "";
    public int? VoltageMillivolts { get; init; }
    public int? MinVoltageMillivolts { get; init; }
    public int? MaxVoltageMillivolts { get; init; }
    public int? MaxCurrentMilliamps { get; init; }
    public int? MaxPowerMilliwatts { get; init; }
    public bool? UsbCommunicationsCapable { get; init; }
    public bool? UnconstrainedPower { get; init; }
    public bool? DualRolePower { get; init; }
    public string Display { get; init; } = "";
    public string Raw { get; init; } = "";
}

public sealed class RequestReport
{
    public int ObjectPosition { get; init; }
    public int OperatingCurrentMilliamps { get; init; }
    public int MaxOperatingCurrentMilliamps { get; init; }
    public int? SelectedVoltageMillivolts { get; init; }
    public int? NegotiatedPowerMilliwatts { get; init; }
    public string Display { get; init; } = "";
    public string Raw { get; init; } = "";
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
    public string? PartnerSourcePdosHex { get; set; }
}

/// <summary>
/// A USB Billboard device: a USB-C adapter declaring which Alternate Modes it supports and
/// whether each was entered. Read over public USB hub IOCTLs, so it needs no special setup.
/// </summary>
public sealed class BillboardReport
{
    public string VendorId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public int PreferredModeIndex { get; init; }
    public List<AlternateModeReport> Modes { get; init; } = [];

    /// <summary>True when a DisplayPort alternate mode is present and was entered successfully.</summary>
    public bool CarriesVideo { get; set; }

    /// <summary>True when DisplayPort is offered at all, whether or not it was entered.</summary>
    public bool SupportsVideo { get; set; }
}

public sealed class AlternateModeReport
{
    public int Index { get; init; }
    public string Svid { get; init; } = "";
    public string Name { get; init; } = "";
    public int ModeNumber { get; init; }
    public string State { get; init; } = "";
    public bool Entered { get; init; }
    public bool IsDisplayPort { get; init; }
}

/// <summary>
/// A USB device as its own descriptors describe it, read through public hub IOCTLs. Needs no
/// elevation and no setup, so this is available on every machine on the first run.
/// </summary>
public sealed class UsbDeviceReport
{
    public string VendorId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string? Manufacturer { get; init; }
    public string? Product { get; init; }
    public string? SerialNumber { get; init; }
    public string DeviceClass { get; init; } = "";
    public byte DeviceClassCode { get; init; }
    public string Speed { get; init; } = "";
    public byte SpeedCode { get; init; }
    public string UsbVersion { get; init; } = "";

    /// <summary>Current the device requests in its configuration descriptor, not what it draws.</summary>
    public int? MaxPowerMilliamps { get; init; }

    public bool IsHub { get; init; }
    public int Address { get; init; }
    public int Port { get; init; }
    public string HubPath { get; init; } = "";

    /// <summary>
    /// True when the device negotiated a slower link than its own declared USB version allows.
    /// The usual cause is a USB 2.0 cable or hub in the chain, and nothing else on the system
    /// tells you this is happening.
    /// </summary>
    public bool IsUnderperforming { get; set; }

    /// <summary>Plain English explanation of the shortfall, null when running at full capability.</summary>
    public string? LinkDiagnosis { get; set; }

    /// <summary>The speed the declared USB version allows, for comparison with <see cref="Speed"/>.</summary>
    public string? ExpectedSpeed { get; set; }
}
