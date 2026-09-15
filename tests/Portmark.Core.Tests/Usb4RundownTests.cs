using Portmark.Core.Usb4;
using Xunit;

namespace Portmark.Core.Tests;

/// <summary>
/// The USB4 rundown parser, over the tracerpt XML of a real elevated capture.
///
/// The fixture came off the ThinkPad T16 Gen 2 (AMD) with nothing attached to its USB4 port, so
/// the USB4 domain was powered down and every link register read zero. That is the capture that
/// matters most: zeros are exactly what a careless decoder turns into "a USB 2 cable" or "a 0 Gbps
/// link", and the test pins that they are reported as no link at all. The two session header
/// events tracerpt adds were trimmed because they carry the capturing user's temp path; every
/// USB4 event is as emitted.
/// </summary>
public class Usb4RundownTests
{
    private static Usb4Report Captured() =>
        Usb4RundownParser.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "usb4-rundown-domain-powered-down.xml")));

    [Fact]
    public void RealCapture_OneDomainOneRootRouterOnePort()
    {
        Usb4Report report = Captured();

        Usb4DomainReport domain = Assert.Single(report.Domains);
        Assert.Equal("0xC06601", domain.DomainId);
        Assert.Equal("0x1022", domain.PciVendorId);
        Assert.Equal("0x1669", domain.PciDeviceId);
        Assert.Equal(@"\_SB_.PCI0.GP19.NHI1", domain.AcpiName);
        Assert.True(domain.PoweredDown);

        Usb4RouterReport router = Assert.Single(domain.Routers);
        Assert.Equal("00-00-00-00-00-00-00", router.TopologyId);
        Assert.True(router.IsHostRouter);
        Assert.Equal("0x438", router.VendorId);
        Assert.Equal("0x20A", router.ProductId);
        Assert.Equal(1, router.Usb4MajorVersion);      // RouterUSB4Version 0x20

        Usb4PortReport port = Assert.Single(router.Ports);
        Assert.Equal(2, port.Lane0AdapterNumber);
        Assert.Equal(3, port.Lane1AdapterNumber);
        Assert.True(port.IsDownstreamFacing);
    }

    [Fact]
    public void HostRouterRundownSeenTwice_IsReportedOnce()
    {
        // The host router provider ran its rundown once when the session was created and again
        // when the device router provider was added. That is one domain, not two.
        Usb4Report report = Captured();

        Assert.Single(report.Domains);
        Assert.True(report.RundownComplete);
    }

    [Fact]
    public void RouterVendorId_CarriesItsRegisteredName()
    {
        // ROUTER_CS_0's vendor ID is a USB-IF vendor ID; 0x438 is registered to AMD, whose host
        // router this is. The PCI vendor ID is a different registry and is not named from this list.
        Usb4RouterReport router = Captured().Domains[0].Routers[0];

        Assert.Equal("Advanced Micro Devices, Inc.", router.RegisteredVendorName);
    }

    [Fact]
    public void ARouterAbsentFromALaterCompleteRundown_IsNotReported()
    {
        string xml = Events(
            Marker(DeviceRouter, "RundownStart"),
            Router(topology: 0), Router(topology: 1),
            Marker(DeviceRouter, "RundownComplete"),
            Marker(DeviceRouter, "RundownStart"),
            Router(topology: 0),
            Marker(DeviceRouter, "RundownComplete"));

        Usb4Report report = Usb4RundownParser.Parse(xml);

        Usb4RouterReport router = Assert.Single(Assert.Single(report.Domains).Routers);
        Assert.True(router.IsHostRouter);
        Assert.True(report.RundownComplete);
        Assert.Null(report.Explanation);
    }

    [Fact]
    public void ACompleteRundownFollowedByAnInterruptedOne_UsesTheCompleteOneAndSaysSo()
    {
        // The earlier RundownComplete must not make the later, cut-off rundown look finished, and
        // the cut-off one must not be mixed into the complete one.
        string xml = Events(
            Marker(DeviceRouter, "RundownStart"),
            Router(topology: 0), Router(topology: 1),
            Marker(DeviceRouter, "RundownComplete"),
            Marker(DeviceRouter, "RundownStart"),
            Router(topology: 0));

        Usb4Report report = Usb4RundownParser.Parse(xml);

        Assert.Equal(2, Assert.Single(report.Domains).Routers.Count);
        Assert.True(report.RundownComplete);
        Assert.NotNull(report.Explanation);
        Assert.Contains("did not finish", report.Explanation);
    }

    [Fact]
    public void AnInterruptedRundownWithNoCompleteOne_IsReportedIncomplete()
    {
        string xml = Events(
            Marker(DeviceRouter, "RundownStart"),
            Router(topology: 0));

        Usb4Report report = Usb4RundownParser.Parse(xml);

        Assert.Single(Assert.Single(report.Domains).Routers);
        Assert.False(report.RundownComplete);
        Assert.NotNull(report.Explanation);
    }

    private const string DeviceRouter = "Microsoft.Windows.USB.USB4.DeviceRouter";

    private static string Events(params string[] events) => $"<Events>{string.Concat(events)}</Events>";

    private static string Marker(string provider, string name) => $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
        	<System><Provider Name="{provider}" /></System>
        	<EventData></EventData>
        	<RenderingInfo Culture="en-GB"><Task>{name}</Task></RenderingInfo>
        </Event>
        """;

    private static string Router(int topology) => $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
        	<System><Provider Name="{DeviceRouter}" /></System>
        	<EventData>
        		<Data Name="IsRundownEvent">1</Data>
        		<Data Name="DomainID">0x1</Data>
        		<Data Name="TopologyID">{topology}</Data><Data Name="TopologyID">0</Data><Data Name="TopologyID">0</Data>
        		<Data Name="TopologyID">0</Data><Data Name="TopologyID">0</Data><Data Name="TopologyID">0</Data>
        		<Data Name="TopologyID">0</Data>
        		<Data Name="VendorID">0x438</Data>
        		<Data Name="ProductID">0x20A</Data>
        	</EventData>
        	<RenderingInfo Culture="en-GB"><Task>DeviceRouterInformation</Task></RenderingInfo>
        </Event>
        """;

    [Fact]
    public void PoweredDownZeros_AreNoLink_NotASlowLinkOrAnOldCable()
    {
        Usb4PortReport port = Captured().Domains[0].Routers[0].Ports[0];

        Assert.False(port.LinkActive);
        Assert.Equal("no USB4 link active", port.LinkState);
        Assert.NotNull(port.Reason);
        Assert.Contains("powered down", port.Reason);

        Assert.Null(port.CurrentLinkSpeed);
        Assert.Null(port.NegotiatedLinkWidth);
        Assert.Null(port.LaneBonded);
        Assert.Null(port.Tbt3CompatibleMode);
        Assert.Null(port.CableUsb4Version);

        // Zero is "disabled" in the adapter state values, but a powered-down domain's zero is not
        // a reading of the adapter, so it is not named.
        Assert.Null(port.AdapterStateName);

        // The zeros themselves are kept, so the reading can be checked.
        Assert.Equal("0x0", port.Raw["CableUsb4Version"]);
        Assert.Equal("0x0", port.Raw["CurrentLinkSpeed"]);
    }

    [Fact]
    public void CapabilityRegisters_AreStillReported_WhileTheLinkIsDown()
    {
        // LANE_ADP_CS_0 is what the port can do, not what it is doing, and it read 0xC and 0x3
        // with the domain powered down: Gen 2 and Gen 3, single and dual lane.
        Usb4PortReport port = Captured().Domains[0].Routers[0].Ports[0];

        Assert.Equal(["Gen 2 (10 Gbps per lane)", "Gen 3 (20 Gbps per lane)"], port.SupportedLinkSpeeds);
        Assert.Equal(["single lane", "dual lane"], port.SupportedLinkWidths);
    }

    [Fact]
    public void Adapters_AreNamedFromTheirTypeAndKeepTheirTunnelFlag()
    {
        Usb4RouterReport router = Captured().Domains[0].Routers[0];

        Assert.Equal([4, 5, 6, 7], router.Adapters.Select(a => a.AdapterNumber));
        Assert.Equal(["USB 3 downstream", "PCIe downstream", "DisplayPort in", "DisplayPort in"],
                     router.Adapters.Select(a => a.Kind));
        Assert.All(router.Adapters, a => Assert.False(a.IsTunneled));
        Assert.Equal("0x1200101", router.Adapters[0].AdapterType);
    }

    [Fact]
    public void NoUsb4Events_IsAnEmptyReportWithAReason()
    {
        Usb4Report report = Usb4RundownParser.Parse("<Events></Events>");

        Assert.Empty(report.Domains);
        Assert.False(report.RundownComplete);
        Assert.NotNull(report.Explanation);
    }

    // Not a hardware capture: no USB4 device has been attached for one yet. This checks only that
    // a live link's registers are decoded per their published definitions, and that the cable
    // version is kept raw rather than given a meaning nobody has published.
    [Fact]
    public void ActiveLink_DecodesSpeedWidthAndMode_KeepsCableVersionRaw()
    {
        Usb4PortReport port = Usb4RundownParser.Parse(SyntheticPort(
            adapterState: "0x2", speed: "0x4", width: "0x2", bonded: "1", tcm: "0", cable: "0x3"))
            .Domains[0].Routers[0].Ports[0];

        Assert.True(port.LinkActive);
        Assert.Equal("up", port.AdapterStateName);
        Assert.Equal("Gen 3 (20 Gbps per lane)", port.CurrentLinkSpeed);
        Assert.Equal("dual lane", port.NegotiatedLinkWidth);
        Assert.True(port.LaneBonded);
        Assert.False(port.Tbt3CompatibleMode);
        Assert.Equal("0x3", port.CableUsb4Version);
        Assert.Null(port.Reason);
    }

    [Fact]
    public void UnrecognisedSpeedOnALiveLink_StaysUnknown()
    {
        Usb4PortReport port = Usb4RundownParser.Parse(SyntheticPort(
            adapterState: "0x2", speed: "0x1", width: "0x1", bonded: "0", tcm: "1", cable: "0x0"))
            .Domains[0].Routers[0].Ports[0];

        Assert.True(port.LinkActive);
        Assert.Null(port.CurrentLinkSpeed);
        Assert.Equal("single lane", port.NegotiatedLinkWidth);
        Assert.True(port.Tbt3CompatibleMode);
    }

    [Fact]
    public void TrainingPort_IsNoLink()
    {
        Usb4PortReport port = Usb4RundownParser.Parse(SyntheticPort(
            adapterState: "0x1", speed: "0x8", width: "0x1", bonded: "0", tcm: "0", cable: "0x0"))
            .Domains[0].Routers[0].Ports[0];

        Assert.False(port.LinkActive);
        Assert.Equal("connecting", port.AdapterStateName);
        Assert.Null(port.CurrentLinkSpeed);
    }

    [Theory]
    [InlineData(0x8, "Gen 2 (10 Gbps per lane)")]
    [InlineData(0x4, "Gen 3 (20 Gbps per lane)")]
    [InlineData(0x2, "Gen 4 (40 Gbps per lane)")]
    [InlineData(0x0, null)]
    [InlineData(0x1, null)]
    public void CurrentLinkSpeed(int raw, string? expected) =>
        Assert.Equal(expected, Usb4Registers.LinkSpeed(raw));

    [Theory]
    [InlineData(0x0, "disabled")]
    [InlineData(0x2, "up")]
    [InlineData(0x7, "unplugged")]
    [InlineData(0x9, null)]
    public void AdapterState(int raw, string? expected) =>
        Assert.Equal(expected, Usb4Registers.AdapterStateName(raw));

    [Theory]
    [InlineData(0x20, 1)]
    [InlineData(0x40, 2)]
    [InlineData(0x00, null)]
    public void RouterUsb4MajorVersion(int raw, int? expected) =>
        Assert.Equal(expected, Usb4Registers.RouterMajorVersion(raw));

    private static string SyntheticPort(string adapterState, string speed, string width, string bonded,
                                        string tcm, string cable) => $"""
        <Events>
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
        	<System><Provider Name="Microsoft.Windows.USB.USB4.DeviceRouter" /></System>
        	<EventData>
        		<Data Name="IsRundownEvent">1</Data>
        		<Data Name="DomainID">0x1</Data>
        		<Data Name="TopologyID">0</Data><Data Name="TopologyID">0</Data><Data Name="TopologyID">0</Data>
        		<Data Name="TopologyID">0</Data><Data Name="TopologyID">0</Data><Data Name="TopologyID">0</Data>
        		<Data Name="TopologyID">0</Data>
        		<Data Name="IsDFP">1</Data>
        		<Data Name="Lane0AdapterNumber">1</Data>
        		<Data Name="Lane1AdapterNumber">2</Data>
        		<Data Name="SupportedLinkSpeeds">0xC</Data>
        		<Data Name="SupportedLinkWidths">0x3</Data>
        		<Data Name="CurrentLinkSpeed">{speed}</Data>
        		<Data Name="NegotiatedLinkWidth">{width}</Data>
        		<Data Name="AdapterState">{adapterState}</Data>
        		<Data Name="LaneBonded">{bonded}</Data>
        		<Data Name="CableUsb4Version">{cable}</Data>
        		<Data Name="Tbt3CompatibleMode">{tcm}</Data>
        	</EventData>
        	<RenderingInfo Culture="en-GB"><Task>PortInformation</Task></RenderingInfo>
        </Event>
        </Events>
        """;
}
