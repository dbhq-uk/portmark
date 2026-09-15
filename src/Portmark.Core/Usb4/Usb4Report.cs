namespace Portmark.Core.Usb4;

/// <summary>
/// What Windows' USB4 drivers said about the USB4 domain in their TraceLogging rundown.
///
/// A separate document from <see cref="Model.PortmarkReport"/>: it comes from a different source
/// (ETW events, which need administrator rights, rather than UCSI), and folding it into the main
/// report would make an ordinary unelevated read look incomplete. The same rule governs it: a
/// field the events did not carry is null, and a zero read from a powered-down domain is reported
/// as no link rather than decoded.
/// </summary>
public sealed class Usb4Report
{
    public string Tool { get; init; } = "portmark";
    public string SchemaVersion { get; init; } = "1";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The ETW providers the events came from.</summary>
    public List<string> Providers { get; init; } =
        [Usb4RundownParser.HostRouterProvider, Usb4RundownParser.DeviceRouterProvider];

    /// <summary>
    /// True when the device router provider marked the end of its rundown. False means the port
    /// and adapter lists may be a prefix of what the driver would have described.
    /// </summary>
    public bool RundownComplete { get; set; }

    /// <summary>Plain English, set when there is something to explain about the whole answer.</summary>
    public string? Explanation { get; set; }

    public List<Usb4DomainReport> Domains { get; init; } = [];
}

/// <summary>One USB4 domain: a host router and every router connected below it.</summary>
public sealed class Usb4DomainReport
{
    /// <summary>The driver's domain ID, as emitted. An identifier, not a register value.</summary>
    public string DomainId { get; init; } = "";

    public string? HostRouterInstancePath { get; set; }
    public string? PciVendorId { get; set; }
    public string? PciDeviceId { get; set; }
    public string? PciSubVendorId { get; set; }
    public string? PciSubSystemId { get; set; }
    public string? AcpiName { get; set; }

    /// <summary>
    /// From the host router's DomainSleepInformation event. Null when that event did not arrive.
    /// While true, the port registers read zero and describe no link.
    /// </summary>
    public bool? PoweredDown { get; set; }

    public List<Usb4RouterReport> Routers { get; init; } = [];

    /// <summary>Every field of the host router events, exactly as tracerpt printed them.</summary>
    public Dictionary<string, string> Raw { get; init; } = [];
}

/// <summary>A router in the domain, from its DeviceRouterInformation event.</summary>
public sealed class Usb4RouterReport
{
    /// <summary>The 7-byte topology ID, as hex bytes. All zeros is the domain's host router.</summary>
    public string TopologyId { get; init; } = "";
    public bool IsHostRouter { get; init; }

    public string? InstancePath { get; set; }
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public string? VendorName { get; set; }
    public string? ModelName { get; set; }
    public string? Uuid { get; set; }

    /// <summary>RouterUSB4Version, raw, and its major version where the encoding is published.</summary>
    public string? RouterUsb4Version { get; set; }
    public int? Usb4MajorVersion { get; set; }

    /// <summary>Kept raw: no published definition says how this value is encoded.</summary>
    public string? ConnectionManagerUsb4Version { get; set; }

    public List<Usb4PortReport> Ports { get; init; } = [];
    public List<Usb4AdapterReport> Adapters { get; init; } = [];

    public Dictionary<string, string> Raw { get; set; } = [];
}

/// <summary>
/// One USB4 port, from its PortInformation event. The capability fields describe what the port
/// can do and are reported whatever the link state. The link fields are null unless the lane
/// adapter reports a link that is up, because on any other state they are not a measurement.
/// </summary>
public sealed class Usb4PortReport
{
    public int? Lane0AdapterNumber { get; init; }
    public int? Lane1AdapterNumber { get; init; }

    /// <summary>IsDFP: true for a downstream-facing port, false for upstream-facing.</summary>
    public bool? IsDownstreamFacing { get; init; }

    public bool LinkActive { get; set; }

    /// <summary>"USB4 link active", "no USB4 link active", or why it cannot be said.</summary>
    public string LinkState { get; set; } = "";

    /// <summary>Why there is no link to describe, when there is none.</summary>
    public string? Reason { get; set; }

    /// <summary>LANE_ADP_CS_1 Adapter State, raw, and its name where the value is defined.</summary>
    public string? AdapterState { get; set; }
    public string? AdapterStateName { get; set; }

    public string? CurrentLinkSpeed { get; set; }
    public string? NegotiatedLinkWidth { get; set; }
    public bool? LaneBonded { get; set; }

    /// <summary>
    /// PORT_CS_18 bit 9, TBT3-Compatible Mode: true when the link runs as Thunderbolt 3 rather
    /// than USB4.
    /// </summary>
    public bool? Tbt3CompatibleMode { get; set; }

    /// <summary>
    /// The port event's CableUsb4Version, raw, and only while a link is up. Deliberately not
    /// decoded: see <see cref="CableUsb4VersionNote"/>.
    /// </summary>
    public string? CableUsb4Version { get; set; }
    public string? CableUsb4VersionNote { get; set; }

    public List<string> SupportedLinkSpeeds { get; set; } = [];
    public List<string> SupportedLinkWidths { get; set; } = [];

    /// <summary>The router on the other end of this port, null when the event reports none.</summary>
    public string? DownstreamRouterTopologyId { get; set; }

    public Dictionary<string, string> Raw { get; init; } = [];
}

/// <summary>A protocol adapter: USB 3, PCIe or DisplayPort, and whether it is tunnelling.</summary>
public sealed class Usb4AdapterReport
{
    /// <summary>The event the adapter was described by, such as DPAdapterInformation.</summary>
    public string Event { get; init; } = "";
    public int? AdapterNumber { get; init; }

    /// <summary>AdapterType as emitted, and its name where the low 24 bits are a defined type.</summary>
    public string? AdapterType { get; init; }
    public string? Kind { get; init; }

    /// <summary>The driver's own IsTunneled flag for this adapter.</summary>
    public bool? IsTunneled { get; init; }

    public Dictionary<string, string> Raw { get; init; } = [];
}
