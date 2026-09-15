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

    /// <summary>
    /// Battery charge as a percentage, null when there is no battery or Windows does not know.
    /// Read only to tell a nearly full battery, which correctly draws very little, apart from one
    /// that is not full and still is not gaining.
    /// </summary>
    public int? BatteryPercent { get; init; }
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

    /// <summary>
    /// The VERSION register as read, for decisions that depend on it. Not serialised: the formatted
    /// <see cref="UcsiVersion"/> already carries it, and the schema is unchanged.
    /// </summary>
    [JsonIgnore]
    public ushort? UcsiVersionBcd { get; set; }
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

    /// <summary>
    /// bmOptionalFeatures bit 8, defined from UCSI 1.2. Clear means the controller will answer
    /// GET_PD_MESSAGE with Not Supported, so the attached device and cable cannot be asked for
    /// their Discover Identity through it.
    /// </summary>
    public bool GetPdMessageSupported { get; init; }
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

    /// <summary>
    /// The connector partner flags from GET_CONNECTOR_STATUS (bits 21-28), raw, and the one bit
    /// that matters here: bit 1, set when the partner is operating in an alternate mode. This is
    /// current operation, where the mode lists are only what is supported. Null when nothing is
    /// attached or the status could not be read.
    /// </summary>
    public int? PartnerFlags { get; set; }
    public bool? PartnerAlternateModeFlag { get; set; }

    /// <summary>
    /// The controller's own view of the charging rate, from bits 64 and 65 of the connector
    /// status. This is the field Windows drives its slow-charging notification from. Microsoft
    /// notes that firmware often leaves it at zero rather than meaning "not charging", so a zero
    /// is reported as the controller's word and never taken as proof the battery is idle.
    /// </summary>
    public int? BatteryChargingStatusCode { get; set; }
    public string? BatteryChargingStatus { get; set; }
    public string? PowerOperationMode { get; set; }
    public string? PowerDirection { get; set; }
    /// <summary>What the connector itself supports, as distinct from what is attached to it.</summary>
    public ConnectorCapabilityReport? Capability { get; set; }

    /// <summary>
    /// The alternate modes this port itself can enter, from GET_ALTERNATE_MODES with the connector
    /// as recipient. Null when the controller does not advertise alternate mode details, so the
    /// question was never asked.
    /// </summary>
    public AlternateModeListReport? SupportedAlternateModes { get; set; }

    /// <summary>
    /// The alternate modes the attached partner offers (recipient SOP). Null when nothing is
    /// attached, or when attachment could not be read, so the partner was never asked.
    /// </summary>
    public AlternateModeListReport? PartnerAlternateModes { get; set; }

    /// <summary>
    /// The offset of the mode in use, and only that: null unless <see cref="ActiveAlternateMode"/>
    /// is set. The raw GET_CURRENT_CAM byte lives in <see cref="RawReport.CurrentCamHex"/>. This
    /// controller returns 0 for an empty port, so that byte alone is not evidence of activity,
    /// and the first release wrongly reported it as such.
    /// </summary>
    public int? ActiveAlternateModeIndex { get; set; }
    public int? SupportedAlternateModeBitmap { get; set; }

    /// <summary>
    /// The mode the controller names as current, when it names one. Read it with
    /// <see cref="ActiveAlternateModeConfirmed"/>: on a controller that never describes the
    /// attached partner, this is the controller's word and nothing else.
    /// </summary>
    public PortAlternateModeReport? ActiveAlternateMode { get; set; }

    /// <summary>
    /// True when something other than the controller's index agrees the mode is in use: either
    /// the partner listed that mode, or the connector status says an alternate mode is in
    /// operation. False means the mode above is unconfirmed, and it is printed as such.
    /// </summary>
    public bool ActiveAlternateModeConfirmed { get; set; }
    public string? AlternateModeNote { get; set; }

    public CableReport Cable { get; set; } = new();

    /// <summary>
    /// What the attached device and cable declare through Discover Identity. Null only when the
    /// connector was never read; otherwise it says whether the question was asked and why not.
    /// </summary>
    public IdentityReport? Identity { get; set; }

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

    /// <summary>
    /// What the power contract implies about the cable when the cable itself cannot be read. Null
    /// unless something can be concluded. Kept in its own object so it can never be mistaken for
    /// a field the cable reported: everything above this line is the cable's own words, and
    /// everything inside it is deduction from someone else's.
    /// </summary>
    public CableInferenceReport? Inferred { get; set; }

    public SpeedReport? Speed { get; set; }
    public int? CurrentCapabilityMilliamps { get; set; }
    public int? MaxWattsAt20Volts { get; set; }
    public string? PlugType { get; set; }
    public bool? ActiveCable { get; set; }
    public bool? VbusInCable { get; set; }

    /// <summary>
    /// UCSI Directionality, byte 3 bit 2: true when the cable's lane directionality is
    /// configurable, false when it is fixed in the cable.
    /// </summary>
    public bool? LaneDirectionalityConfigurable { get; set; }

    /// <summary>
    /// UCSI Mode Support. Only valid for an active cable, so it is null for a passive one, with
    /// <see cref="AlternateModeSupportNote"/> saying why.
    /// </summary>
    public bool? SupportsAlternateModes { get; set; }
    public string? AlternateModeSupportNote { get; set; }
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

    /// <summary>
    /// True when this PC is drawing power and took less than half of what the supply offers. A
    /// measurement, not a verdict: a nearly full battery draws little and that is correct.
    /// </summary>
    public bool IsUnderNegotiated { get; set; }

    /// <summary>Plain English account of the gap, null when there is nothing to explain.</summary>
    public string? PowerDiagnosis { get; set; }
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
    public string? CurrentCamHex { get; set; }
    public string? CurrentCamCci { get; set; }

    /// <summary>
    /// Every GET_ALTERNATE_MODES request made for this connector, in order, recorded before any
    /// interpretation. Failed requests are kept too: an empty list and a refused list must stay
    /// distinguishable by anyone re-reading the bytes.
    /// </summary>
    public List<UcsiExchangeReport> AlternateModeExchanges { get; set; } = [];

    /// <summary>
    /// Every GET_PD_MESSAGE request made for this connector, in order, recorded before any
    /// interpretation. Empty when the controller does not offer the command, because then it is
    /// never sent, and <see cref="IdentityReport.Reason"/> says so.
    /// </summary>
    public List<UcsiExchangeReport> PdMessageExchanges { get; set; } = [];
}

/// <summary>
/// What the power contract says about the cable. Not the cable's own words: a deduction from the
/// supply's advertisement, stated with the evidence it rests on and the ambiguity it cannot
/// resolve. This is the one place portmark concludes something it was not told directly, and it
/// says so in every field.
/// </summary>
public sealed class CableInferenceReport
{
    /// <summary>The current the cable must be able to carry, in milliamps.</summary>
    public int? MinimumCurrentRatingMilliamps { get; set; }

    /// <summary>What was observed.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>The rule that makes the observation mean something.</summary>
    public string Basis { get; set; } = "";

    /// <summary>What follows, and what does not.</summary>
    public string Conclusion { get; set; } = "";
}

/// <summary>One UCSI request and its answer, exactly as exchanged.</summary>
public sealed class UcsiExchangeReport
{
    public string Command { get; init; } = "";
    public string ControlHex { get; init; } = "";
    public string? Cci { get; init; }
    public string? PayloadHex { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// One list of alternate modes as enumerated over UCSI. An empty <see cref="Modes"/> with
/// <see cref="Complete"/> true is a real answer: the controller listed nothing. An empty list with
/// <see cref="DataAvailable"/> false is not an answer, and <see cref="Reason"/> says what happened.
/// A non-empty list with <see cref="Complete"/> false is a prefix: the controller stopped
/// answering, or stopped moving, part way through.
/// </summary>
public sealed class AlternateModeListReport
{
    public bool DataAvailable { get; set; }
    public bool Complete { get; set; }
    public string? Reason { get; set; }
    public List<PortAlternateModeReport> Modes { get; set; } = [];
}

/// <summary>
/// One alternate mode as UCSI lists it: an SVID and the 32-bit "MID" the controller returned.
/// UCSI calls the second field a mode ID; Linux treats it as the mode's Discover Modes VDO, and
/// that is the only interoperable reading. It is kept raw. This controller returns 0x00000001 for
/// Thunderbolt (the TBT mode bit) and 0x00000003 for DisplayPort (DFP_D and UFP_D capable, no
/// pin assignments), which are abbreviated VDOs at best, so nothing is derived from them.
/// </summary>
public sealed class PortAlternateModeReport
{
    public int Offset { get; init; }
    public string Svid { get; init; } = "";
    public string Name { get; init; } = "";
    public string ModeId { get; init; } = "";
    public bool IsDisplayPort { get; init; }
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

/// <summary>One node in the USB device tree: a hub, or a device attached to one.</summary>
public sealed class UsbTreeNode
{
    /// <summary>The device this node represents. Null for a host controller's root hub.</summary>
    public UsbDeviceReport? Device { get; set; }

    public string Label { get; set; } = "";
    public bool IsRootHub { get; set; }
    public List<UsbTreeNode> Children { get; set; } = [];

    /// <summary>Set internally once this node's ports have been merged into its device node.</summary>
    public bool Adopted { get; set; }

    /// <summary>
    /// True when several identical hubs made it impossible to tell which interface belongs to
    /// this one. The node is left unnested rather than attached to a guess.
    /// </summary>
    public bool AmbiguousTopology { get; set; }
}

/// <summary>
/// What the devices on one hub have asked for, against what that hub can supply.
///
/// The figures are requests, not measurements: bMaxPower is what a device asks for in its
/// configuration descriptor, and a device may request 500 mA and idle at 50. Over-subscription
/// here is therefore a real risk rather than a measured fault, and is worded that way.
/// </summary>
public sealed class HubPowerReport
{
    public string HubPath { get; init; } = "";
    public bool IsRootHub { get; init; }
    public bool IsBusPowered { get; init; }
    public int PortCount { get; init; }
    public int DeviceCount { get; init; }

    /// <summary>Current the hub's own electronics consume, from its descriptor.</summary>
    public int HubControlCurrentMilliamps { get; init; }

    /// <summary>
    /// The shared budget, which only a bus-powered hub has. Null for a self-powered hub, which
    /// has its own supply and a per-port guarantee rather than a pool to divide up.
    /// </summary>
    public int? AvailableMilliamps { get; init; }
    public int RequestedMilliamps { get; init; }
    public int PerPortAllowanceMilliamps { get; init; }

    /// <summary>True when the attached devices have requested more than this hub promises.</summary>
    public bool OverSubscribed { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// What the attached device and the cable say about themselves through USB PD Discover Identity,
/// read with UCSI GET_PD_MESSAGE. Everything under here is a declaration by the device or cable,
/// passed on, and never a measurement.
/// </summary>
public sealed class IdentityReport
{
    /// <summary>
    /// Whether GET_PD_MESSAGE was sent. False is the usual answer: it is only sent when the
    /// controller reports UCSI 1.2 or later, advertises the command, and something is attached.
    /// </summary>
    public bool Requested { get; set; }

    /// <summary>Why the question was not asked, when it was not.</summary>
    public string? Reason { get; set; }

    /// <summary>The Discover Identity response from the attached device (SOP).</summary>
    public DiscoverIdentityReport? Partner { get; set; }

    /// <summary>The Discover Identity response from the cable plug (SOP').</summary>
    public DiscoverIdentityReport? Cable { get; set; }
}

/// <summary>
/// One Discover Identity response. <see cref="ObjectsHex"/> holds every object as returned, VDM
/// Header first, so the decoding below it can be redone by hand.
/// </summary>
public sealed class DiscoverIdentityReport
{
    /// <summary>"SOP" for the attached device, "SOP'" for the cable plug.</summary>
    public string Recipient { get; set; } = "";

    public bool DataAvailable { get; set; }

    /// <summary>
    /// True when every object the ID Header calls for was returned in the expected layout, false
    /// when some were not, and null when that cannot be judged.
    /// </summary>
    public bool? Complete { get; set; }

    /// <summary>Why there is no identity, or what about this one is incomplete or undecoded.</summary>
    public string? Reason { get; set; }

    /// <summary>ACK, NAK, BUSY or REQ, from the Structured VDM Header.</summary>
    public string? CommandType { get; set; }
    public string? StructuredVdmVersion { get; set; }

    public List<string> ObjectsHex { get; set; } = [];

    public IdHeaderReport? IdHeader { get; set; }
    public string? CertStatXid { get; set; }
    public ProductVdoReport? Product { get; set; }
    public UfpVdoReport? Ufp { get; set; }
    public DfpVdoReport? Dfp { get; set; }
    public PassiveCableVdoReport? PassiveCable { get; set; }
    public ActiveCableVdoReport? ActiveCable { get; set; }

    /// <summary>
    /// A VCONN Powered USB Device answers on SOP' as a cable plug does, but it is not a cable, and
    /// it is kept out of the cable fields.
    /// </summary>
    public VconnPoweredDeviceVdoReport? VconnPoweredDevice { get; set; }

    /// <summary>The plain English sentence, worded as what the device or cable declares.</summary>
    public string? Declaration { get; set; }
}

/// <summary>
/// One enumerated field. <see cref="Status"/> is "defined", "reserved" (the specification gives
/// the value no meaning, or calls it invalid) or "deprecated" (it had a meaning once, stated in
/// <see cref="Meaning"/>). A reserved value is never mapped onto a nearby defined one.
/// </summary>
public sealed class IdentityCode
{
    public int Code { get; init; }
    public string Meaning { get; init; } = "";
    public string Status { get; init; } = "";
}

public sealed class IdHeaderReport
{
    public bool UsbHostCapable { get; set; }
    public bool UsbDeviceCapable { get; set; }

    /// <summary>Product Type (UFP) for the attached device, Product Type (Cable Plug/VPD) on SOP'.</summary>
    public IdentityCode ProductType { get; set; } = new();
    public bool ModalOperationSupported { get; set; }

    /// <summary>Null on SOP', where the field is reserved, and in Structured VDM Version 1.0.</summary>
    public IdentityCode? ProductTypeDfp { get; set; }
    public IdentityCode? ConnectorType { get; set; }
    public string VendorId { get; set; } = "";
    public string Raw { get; set; } = "";
}

public sealed class ProductVdoReport
{
    public string ProductId { get; set; } = "";
    public string BcdDevice { get; set; } = "";
    public string Raw { get; set; } = "";
}

public sealed class UfpVdoReport
{
    public IdentityCode VdoVersion { get; set; } = new();
    public bool? Usb4DeviceCapable { get; set; }
    public bool? Usb32DeviceCapable { get; set; }
    public IdentityCode? Usb20DeviceCapability { get; set; }
    public bool? NonReconfiguringAlternateModesSupported { get; set; }
    public bool? ReconfiguringAlternateModesSupported { get; set; }
    public bool? Tbt3AlternateModeSupported { get; set; }
    public bool? VbusRequired { get; set; }
    public bool? VconnRequired { get; set; }
    public IdentityCode? VconnPower { get; set; }
    public IdentityCode? HighestSpeed { get; set; }
    public string? Note { get; set; }
    public string Raw { get; set; } = "";
}

public sealed class DfpVdoReport
{
    public IdentityCode VdoVersion { get; set; } = new();
    public bool? Usb4HostCapable { get; set; }
    public bool? Usb32HostCapable { get; set; }
    public bool? Usb20HostCapable { get; set; }
    public int? PortNumber { get; set; }
    public string? Note { get; set; }
    public string Raw { get; set; } = "";
}

public sealed class PassiveCableVdoReport
{
    public int HardwareVersion { get; set; }
    public int FirmwareVersion { get; set; }
    public IdentityCode VdoVersion { get; set; } = new();
    public IdentityCode? PlugType { get; set; }
    public bool? EprCapable { get; set; }
    public IdentityCode? Latency { get; set; }
    public IdentityCode? TerminationType { get; set; }
    public IdentityCode? MaxVbusVoltage { get; set; }

    /// <summary>Null unless the code is a defined one: a deprecated code is not a voltage the cable stated.</summary>
    public int? MaxVbusVolts { get; set; }
    public IdentityCode? CurrentHandling { get; set; }
    public int? MaxCurrentMilliamps { get; set; }
    public IdentityCode? HighestSpeed { get; set; }
    public string? Note { get; set; }
    public string Raw { get; set; } = "";
}

public sealed class ActiveCableVdoReport
{
    public int HardwareVersion { get; set; }
    public int FirmwareVersion { get; set; }
    public IdentityCode VdoVersion { get; set; } = new();
    public IdentityCode? PlugType { get; set; }
    public bool? EprCapable { get; set; }
    public IdentityCode? Latency { get; set; }
    public IdentityCode? TerminationType { get; set; }
    public IdentityCode? MaxVbusVoltage { get; set; }
    public int? MaxVbusVolts { get; set; }
    public bool? SbuSupported { get; set; }
    public IdentityCode? SbuType { get; set; }
    public bool? VbusThroughCable { get; set; }
    public IdentityCode? CurrentHandling { get; set; }
    public int? MaxCurrentMilliamps { get; set; }
    public bool? SopDoublePrimeControllerPresent { get; set; }
    public IdentityCode? HighestSpeed { get; set; }

    // Active Cable VDO2. Null when the second VDO was not returned.
    public int? MaxOperatingTemperatureCelsius { get; set; }
    public int? ShutdownTemperatureCelsius { get; set; }
    public IdentityCode? U3CldPower { get; set; }
    public bool? U3ToU0ThroughU3S { get; set; }
    public IdentityCode? PhysicalConnection { get; set; }
    public IdentityCode? ActiveElement { get; set; }
    public bool? Usb4Supported { get; set; }
    public int? Usb2HubHopsConsumed { get; set; }
    public bool? Usb2Supported { get; set; }
    public bool? Usb32Supported { get; set; }
    public IdentityCode? LanesSupported { get; set; }
    public bool? OpticallyIsolated { get; set; }
    public bool? Usb4AsymmetricModeSupported { get; set; }
    public IdentityCode? UsbGen { get; set; }

    public string? Note { get; set; }
    public string Raw { get; set; } = "";
    public string? Raw2 { get; set; }
}

public sealed class VconnPoweredDeviceVdoReport
{
    public int HardwareVersion { get; set; }
    public int FirmwareVersion { get; set; }
    public IdentityCode VdoVersion { get; set; } = new();
    public IdentityCode? MaxVbusVoltage { get; set; }
    public int? MaxVbusVolts { get; set; }
    public bool? ChargeThroughSupported { get; set; }
    public IdentityCode? ChargeThroughCurrent { get; set; }
    public int? VbusImpedanceMilliohms { get; set; }
    public int? GroundImpedanceMilliohms { get; set; }
    public string? Note { get; set; }
    public string Raw { get; set; } = "";
}
