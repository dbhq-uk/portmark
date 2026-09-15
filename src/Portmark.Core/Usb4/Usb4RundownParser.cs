using System.Globalization;
using System.Xml.Linq;
using Portmark.Core.Usb;

namespace Portmark.Core.Usb4;

/// <summary>
/// Turns tracerpt's XML dump of the USB4 rundown into a <see cref="Usb4Report"/>. Pure: no
/// hardware, no files, so it is tested over a real capture.
///
/// The event and field names are the ones the drivers actually emitted on this machine, which
/// differ from Microsoft's field table in one place that matters: the port's cable field is
/// emitted as CableUsb4Version, not the documented CableUsbVersion. The documented name is
/// accepted too, in case another build of the driver follows the page.
/// </summary>
public static class Usb4RundownParser
{
    public const string HostRouterProvider = "Microsoft.Windows.USB.USB4.HostRouter";
    public const string DeviceRouterProvider = "Microsoft.Windows.USB.USB4.DeviceRouter";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    private const string CableVersionNote =
        "Copied from PORT_CS_18[7:0], Cable USB4 Version. Microsoft's field table types it as a "
      + "Boolean and publishes no encoding for the eight bits, so portmark keeps the value raw "
      + "and does not say what version of cable it means.";

    private sealed record TraceEvent(string Provider, string Name, List<KeyValuePair<string, string>> Fields)
    {
        public string? Get(string name) => Fields.FirstOrDefault(f => f.Key == name).Value;

        public Dictionary<string, string> Raw()
        {
            var raw = new Dictionary<string, string>();
            foreach (IGrouping<string, string> g in Fields.GroupBy(f => f.Key, f => f.Value))
                raw[g.Key] = string.Join(",", g);
            return raw;
        }
    }

    /// <summary>One provider's events chosen to describe the domain, and whether they are a whole rundown.</summary>
    private sealed record Snapshot(List<TraceEvent> Events, bool Complete, bool LaterRundownInterrupted);

    public static Usb4Report Parse(string xml)
    {
        List<TraceEvent> all = ReadEvents(XDocument.Parse(xml));
        Snapshot host = LatestSnapshot(all.Where(e => e.Provider == HostRouterProvider));
        Snapshot device = LatestSnapshot(all.Where(e => e.Provider == DeviceRouterProvider));
        List<TraceEvent> events = [.. host.Events, .. device.Events];

        var report = new Usb4Report { RundownComplete = device.Complete };

        // Enabling each provider makes the drivers emit a fresh rundown, so the same domain,
        // router, port or adapter can be described more than once in one capture. The host
        // router's rundown appeared twice on this machine. Only one rundown per provider is used
        // (see LatestSnapshot), so within it each thing is described once; the replacing below
        // only guards against a driver repeating itself inside one rundown.
        var domains = new Dictionary<string, Usb4DomainReport>();
        var routers = new Dictionary<(string, string), Usb4RouterReport>();
        var ports = new Dictionary<(string, string, string), TraceEvent>();
        var adapters = new Dictionary<(string, string, string), TraceEvent>();

        Usb4DomainReport DomainFor(string id)
        {
            if (!domains.TryGetValue(id, out Usb4DomainReport? d))
                domains[id] = d = new Usb4DomainReport { DomainId = id };
            return d;
        }

        Usb4RouterReport RouterFor(string domainId, string topology)
        {
            if (!routers.TryGetValue((domainId, topology), out Usb4RouterReport? r))
            {
                routers[(domainId, topology)] = r = new Usb4RouterReport
                {
                    TopologyId = topology,
                    // The host router's route string is zero; Microsoft's table likewise uses an
                    // all-zero topology ID to mean "no device router" downstream of a port.
                    IsHostRouter = topology.All(c => c is '0' or '-'),
                };
                DomainFor(domainId).Routers.Add(r);
            }
            return r;
        }

        foreach (TraceEvent e in events)
        {
            string? domainId = e.Get("DomainID");
            if (domainId is null) continue;

            switch (e.Name)
            {
                case "HostRouterInformationPci":
                {
                    Usb4DomainReport d = DomainFor(domainId);
                    d.HostRouterInstancePath = e.Get("DeviceInstancePath");
                    d.PciVendorId = e.Get("VendorId");
                    d.PciDeviceId = e.Get("DeviceId");
                    d.PciSubVendorId = e.Get("SubVendorId");
                    d.PciSubSystemId = e.Get("SubSystemId");
                    d.AcpiName = NullIfEmpty(e.Get("AcpiBiosDeviceName"));
                    foreach (KeyValuePair<string, string> kv in e.Raw()) d.Raw[kv.Key] = kv.Value;
                    break;
                }
                case "DomainSleepInformation":
                {
                    Usb4DomainReport d = DomainFor(domainId);
                    d.PoweredDown = ParseBool(e.Get("IsDomainPoweredDown"));
                    foreach (KeyValuePair<string, string> kv in e.Raw()) d.Raw[kv.Key] = kv.Value;
                    break;
                }
                case "DeviceRouterInformation":
                {
                    Usb4RouterReport r = RouterFor(domainId, Topology(e, "TopologyID"));
                    r.InstancePath = e.Get("DeviceInstancePath");
                    r.VendorId = e.Get("VendorID") ?? e.Get("VendorId");
                    // ROUTER_CS_0's Vendor ID is USB-IF assigned, so the USB list names it. tracerpt
                    // prints it without leading zeros ("0x438"), which VendorNames.Find(string) rejects.
                    r.RegisteredVendorName = ParseLong(r.VendorId) is long vid and >= 0 and <= 0xFFFF
                        ? VendorNames.Find((ushort)vid)
                        : null;
                    r.ProductId = e.Get("ProductID") ?? e.Get("ProductId");
                    r.VendorName = NullIfEmpty(e.Get("AsciiVendorName"));
                    r.ModelName = NullIfEmpty(e.Get("AsciiModelName"));
                    r.Uuid = e.Get("UUID");
                    r.RouterUsb4Version = e.Get("RouterUSB4Version");
                    r.Usb4MajorVersion = ParseInt(r.RouterUsb4Version) is int v ? Usb4Registers.RouterMajorVersion(v) : null;
                    r.ConnectionManagerUsb4Version = e.Get("ConnectionManagerUSB4Version");
                    r.Raw = e.Raw();
                    break;
                }
                case "PortInformation":
                    ports[(domainId, Topology(e, "TopologyID"), e.Get("Lane0AdapterNumber") ?? "")] = e;
                    break;
                case "USB3AdapterInformation" or "PCIeAdapterInformation"
                  or "DPAdapterInformation" or "OtherAdapterInformation":
                    adapters[(domainId, Topology(e, "TopologyID"), e.Get("AdapterNumber") ?? "")] = e;
                    break;
            }
        }

        // Ports are built last, because the domain's sleep state decides how their zeros read
        // and nothing guarantees it arrives first.
        foreach (((string domainId, string topology, _), TraceEvent e) in ports)
            RouterFor(domainId, topology).Ports.Add(BuildPort(e, DomainFor(domainId).PoweredDown));

        foreach (((string domainId, string topology, _), TraceEvent e) in adapters)
        {
            string? type = e.Get("AdapterType");
            RouterFor(domainId, topology).Adapters.Add(new Usb4AdapterReport
            {
                Event = e.Name,
                AdapterNumber = ParseInt(e.Get("AdapterNumber")),
                AdapterType = type,
                Kind = ParseLong(type) is long t ? Usb4Registers.AdapterKind(t) : null,
                IsTunneled = ParseBool(e.Get("IsTunneled")),
                Raw = e.Raw(),
            });
        }

        foreach (Usb4RouterReport r in routers.Values)
            r.Adapters.Sort((a, b) => (a.AdapterNumber ?? int.MaxValue).CompareTo(b.AdapterNumber ?? int.MaxValue));

        report.Domains.AddRange(domains.Values);

        var explanation = new List<string>();
        if (report.Domains.Count == 0)
            explanation.Add(
                "Windows' USB4 drivers described no USB4 domain. This PC may have no USB4 host "
              + "router, or one that Windows' USB4 drivers do not manage; the events cannot tell "
              + "those apart.");
        else if (!report.RundownComplete)
            explanation.Add(
                "The device router rundown did not finish inside the collection window, so the "
              + "routers, ports and adapters below may not be all of them.");

        if (host.LaterRundownInterrupted || device.LaterRundownInterrupted)
            explanation.Add(
                "The USB4 drivers began describing the domain again during the capture and that later "
              + "description did not finish, so what is below is from the last description that did.");

        report.Explanation = explanation.Count > 0 ? string.Join(" ", explanation) : null;
        return report;
    }

    /// <summary>
    /// Picks the one rundown of a provider's events that the report describes. Each rundown sits
    /// between RundownStart and RundownComplete; the last complete one is used, because merging
    /// rundowns kept a router that a later rundown no longer described, and treating any
    /// RundownComplete as the end made a later rundown cut off by the window look finished. When
    /// no rundown completed, the last one is used and reported incomplete. Events outside any
    /// rundown are only used when the capture has no rundown markers at all, since they cannot be
    /// placed before or after a particular description.
    /// </summary>
    private static Snapshot LatestSnapshot(IEnumerable<TraceEvent> providerEvents)
    {
        var rundowns = new List<(List<TraceEvent> Events, bool Complete)>();
        var current = new List<TraceEvent>();
        bool started = false;

        foreach (TraceEvent e in providerEvents)
        {
            switch (e.Name)
            {
                case "RundownStart":
                    if (started) rundowns.Add((current, false));
                    current = [];
                    started = true;
                    break;
                case "RundownComplete":
                    // A completion whose start was not seen is not a whole rundown.
                    rundowns.Add((current, started));
                    current = [];
                    started = false;
                    break;
                default:
                    current.Add(e);
                    break;
            }
        }
        if (started) rundowns.Add((current, false));

        if (rundowns.Count == 0) return new Snapshot(current, false, false);

        int lastComplete = rundowns.FindLastIndex(r => r.Complete);
        return lastComplete < 0
            ? new Snapshot(rundowns[^1].Events, false, false)
            : new Snapshot(rundowns[lastComplete].Events, true, lastComplete < rundowns.Count - 1);
    }

    private static Usb4PortReport BuildPort(TraceEvent e, bool? domainPoweredDown)
    {
        int? state = ParseInt(e.Get("AdapterState"));
        string? stateName = state is int s ? Usb4Registers.AdapterStateName(s) : null;
        bool up = state is int st && Usb4Registers.IsLinkUp(st);

        var port = new Usb4PortReport
        {
            Lane0AdapterNumber = ParseInt(e.Get("Lane0AdapterNumber")),
            Lane1AdapterNumber = ParseInt(e.Get("Lane1AdapterNumber")),
            IsDownstreamFacing = ParseBool(e.Get("IsDFP")),
            AdapterState = e.Get("AdapterState"),
            AdapterStateName = stateName,
            SupportedLinkSpeeds = ParseInt(e.Get("SupportedLinkSpeeds")) is int ss ? Usb4Registers.SupportedLinkSpeeds(ss) : [],
            SupportedLinkWidths = ParseInt(e.Get("SupportedLinkWidths")) is int sw ? Usb4Registers.SupportedLinkWidths(sw) : [],
            Raw = e.Raw(),
        };

        string downstream = Topology(e, "DownstreamRouterTopologyID");
        if (downstream.Length > 0 && !downstream.All(c => c is '0' or '-'))
            port.DownstreamRouterTopologyId = downstream;

        // An all-zero port event is what a powered-down domain emits. Decoded, it would read as a
        // disabled adapter on a 0 Gbps link with a version-0 cable. None of that was measured:
        // there is no link, and every link field stays null. That includes the state's name: zero
        // is "disabled" in the register values, but it was not read from a live adapter.
        if (domainPoweredDown == true) port.AdapterStateName = null;

        if (domainPoweredDown == true && up)
        {
            port.LinkState = "link state uncertain";
            port.Reason = $"The port reports its lane adapter as '{stateName}', but the host router "
                        + "reported the USB4 domain as powered down. The two readings disagree, so "
                        + "neither is reported as the link.";
            return port;
        }
        if (domainPoweredDown == true)
        {
            port.LinkState = "no USB4 link active";
            port.Reason = "The host router reported the USB4 domain as powered down, so this port's "
                        + "registers read zero. That describes no link, and says nothing about a "
                        + "cable or anything plugged in.";
            return port;
        }
        if (state is null)
        {
            port.LinkState = "link state not reported";
            port.Reason = "The port event carried no adapter state, so whether a link is up cannot be said.";
            return port;
        }
        if (stateName is null)
        {
            port.LinkState = "link state not recognised";
            port.Reason = $"The lane adapter state {e.Get("AdapterState")} is not a defined value, so "
                        + "whether a link is up cannot be said.";
            return port;
        }
        if (!up)
        {
            port.LinkState = "no USB4 link active";
            port.Reason = $"The port reports its lane adapter as '{stateName}'. Link speed, width, "
                        + "TBT3 mode and the cable field only describe a link that is up.";
            return port;
        }

        port.LinkActive = true;
        port.LinkState = "USB4 link active";
        port.CurrentLinkSpeed = ParseInt(e.Get("CurrentLinkSpeed")) is int cs ? Usb4Registers.LinkSpeed(cs) : null;
        port.NegotiatedLinkWidth = ParseInt(e.Get("NegotiatedLinkWidth")) is int w ? Usb4Registers.LinkWidth(w) : null;
        port.LaneBonded = ParseBool(e.Get("LaneBonded"));
        port.Tbt3CompatibleMode = ParseBool(e.Get("Tbt3CompatibleMode"));
        port.CableUsb4Version = e.Get("CableUsb4Version") ?? e.Get("CableUsbVersion");
        if (port.CableUsb4Version is not null) port.CableUsb4VersionNote = CableVersionNote;
        return port;
    }

    private static List<TraceEvent> ReadEvents(XDocument doc)
    {
        var events = new List<TraceEvent>();
        foreach (XElement ev in doc.Descendants(Ns + "Event"))
        {
            string? provider = (string?)ev.Element(Ns + "System")?.Element(Ns + "Provider")?.Attribute("Name");
            if (provider is not (HostRouterProvider or DeviceRouterProvider)) continue;

            // tracerpt puts the TraceLogging event name in RenderingInfo/Task.
            string? name = ev.Element(Ns + "RenderingInfo")?.Element(Ns + "Task")?.Value.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var fields = new List<KeyValuePair<string, string>>();
            foreach (XElement data in ev.Element(Ns + "EventData")?.Elements(Ns + "Data") ?? [])
            {
                string? key = (string?)data.Attribute("Name");
                if (!string.IsNullOrEmpty(key)) fields.Add(new(key, data.Value.Trim()));
            }
            events.Add(new TraceEvent(provider, name, fields));
        }
        return events;
    }

    /// <summary>A 7-byte ID emitted as seven repeated fields, as hex bytes joined by hyphens.</summary>
    private static string Topology(TraceEvent e, string field) =>
        string.Join("-", e.Fields.Where(f => f.Key == field)
                                 .Select(f => ParseInt(f.Value) is int b ? b.ToString("X2") : "??"));

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => null,
    };

    private static int? ParseInt(string? value) => ParseLong(value) is long l and >= int.MinValue and <= int.MaxValue ? (int)l : null;

    private static long? ParseLong(string? value)
    {
        if (value is null) return null;
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex) ? hex : null;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dec) ? dec : null;
    }
}
