using Portmark.Core.Model;
using Portmark.Core.Ucsi;
using Xunit;

namespace Portmark.Core.Tests;

/// <summary>
/// Tests built on bytes actually captured from hardware, not invented ones.
///
/// Every vector here came off a Lenovo ThinkPad T16 Gen 2 with a 65W USB-C charger and a USB-C
/// dock attached. Where a vector can be checked against physical reality it is: the power data
/// objects decode to the profile printed on the charger, and the connector status matches which
/// port the charger was in. That is what makes these regression tests rather than a restatement
/// of the same assumptions the decoder already makes.
/// </summary>
public class CablePropertyTests
{
    [Fact]
    public void AllZeroResponseIsReportedAsAbsent_NotAsAPassiveTypeAcable()
    {
        // The real GET_CABLE_PROPERTY response on hardware that does not support the command.
        // Decoding these as fields would invent a passive USB Type-A cable rated 0 mA.
        byte[] empty = [0, 0, 0, 0, 0];

        CableReport report = CableProperty.Decode(empty);

        Assert.False(report.DataAvailable);
        Assert.NotNull(report.Reason);
        Assert.Null(report.Speed);
        Assert.Null(report.CurrentCapabilityMilliamps);
        Assert.Null(report.PlugType);
    }

    [Fact]
    public void VideoIsNeverClaimedFromCableProperty()
    {
        // UCSI GET_CABLE_PROPERTY carries no video capability field at all, so the answer must
        // stay null whether or not the rest of the response decodes.
        byte[] populated = [0x2C, 0x81, 0x64, 0x1A, 0x02];

        CableReport report = CableProperty.Decode(populated);

        Assert.Null(report.SupportsVideo);
        Assert.NotNull(report.VideoNote);
    }

    [Theory]
    [InlineData(0x0000, null)]                    // no speed reported is not "slow"
    [InlineData(0x8001, "1 Mbps")]                // exponent 2, mantissa 1
    [InlineData(0xC00A, "10 Gbps")]               // exponent 3, mantissa 10
    public void SpeedMantissaAndExponentDecode(ushort raw, string? expected)
    {
        SpeedReport? speed = CableProperty.DecodeSpeed(raw);
        Assert.Equal(expected, speed?.Display);
    }

    [Fact]
    public void CurrentOfZeroMeansNotReported()
    {
        // A cable reporting no current rating is different from a cable rated at zero amps.
        byte[] noCurrent = [0x01, 0x00, 0x00, 0x10, 0x00];

        CableReport report = CableProperty.Decode(noCurrent);

        Assert.True(report.DataAvailable);
        Assert.Null(report.CurrentCapabilityMilliamps);
        Assert.Null(report.MaxWattsAt20Volts);
    }
}

public class CapabilityTests
{
    /// <summary>Real GET_CAPABILITY response from the test machine.</summary>
    private static readonly byte[] Captured =
        Convert.FromHexString("46400000029400000300020100020001");

    [Fact]
    public void ConnectorCountDecodes()
    {
        Assert.Equal(2, Capability.ConnectorCount(Captured));
    }

    [Fact]
    public void CableDetailsUnsupportedIsDetected()
    {
        // bmOptionalFeatures is 0x000094: bits 2, 4 and 7 set, bit 5 clear. Bit 5 is
        // CableDetailsAvailable, and its absence is why this machine can never report cables.
        PpmFeatureReport? features = Capability.Decode(Captured);

        Assert.NotNull(features);
        Assert.Equal("0x000094", features.OptionalFeaturesHex);
        Assert.False(features.CableDetailsAvailable);
        Assert.True(features.AlternateModeDetailsAvailable);
        Assert.True(features.PowerDataObjectDetailsAvailable);
    }

    [Fact]
    public void VersionsDecodeFromBcd()
    {
        PpmFeatureReport? features = Capability.Decode(Captured);

        Assert.NotNull(features);
        Assert.Equal(3, features.AlternateModeCount);
        Assert.Equal("2.0", features.PowerDeliveryVersion);
        Assert.Equal("1.0", features.TypeCVersion);
        // 0x0102 in BCD is 1.0.2, not 1.2. This firmware most likely intends Battery Charging
        // 1.2 and has encoded it non-standardly, but the decoder reports what was sent rather
        // than what was probably meant.
        Assert.Equal("1.0.2", features.BatteryChargingVersion);
    }

    [Fact]
    public void ShortResponseDecodesToNothingRatherThanGuessing()
    {
        Assert.Null(Capability.Decode([0x01, 0x02]));
    }
}

public class ConnectorStatusTests
{
    [Fact]
    public void ChargerPortDecodesAsConsumingOverPowerDelivery()
    {
        // Connector 1 on the test machine, with the 65W charger attached.
        byte[] captured = Convert.FromHexString("00002B202CB1041301");
        var report = new ConnectorReport { Index = 1 };

        ConnectorStatus.Apply(captured, report);

        Assert.True(report.Connected);
        Assert.Equal("Downstream facing port", report.PartnerType);
        Assert.Equal("USB Power Delivery", report.PowerOperationMode);
        Assert.Equal("consuming", report.PowerDirection);
    }

    [Fact]
    public void DockPortDecodesAsSupplyingPower()
    {
        // Connector 2 on the test machine, with the dock attached.
        byte[] captured = Convert.FromHexString("00003B402CB1041300");
        var report = new ConnectorReport { Index = 2 };

        ConnectorStatus.Apply(captured, report);

        Assert.True(report.Connected);
        Assert.Equal("Upstream facing port", report.PartnerType);
        Assert.Equal("supplying", report.PowerDirection);
    }

    [Fact]
    public void UnreadableStatusLeavesConnectedUnknownRatherThanFalse()
    {
        // "We could not tell" must not collapse into "nothing is attached".
        var report = new ConnectorReport { Index = 1 };

        ConnectorStatus.Apply([0x00], report);

        Assert.Null(report.Connected);
    }
}

public class ConnectorCapabilityTests
{
    [Fact]
    public void PortCapabilitiesDecode()
    {
        // Connector 1 on the test machine: 0xE443.
        ConnectorCapabilityReport? report =
            ConnectorCapability.Decode(Convert.FromHexString("E443"));

        Assert.NotNull(report);
        Assert.True(report.SupportsUsb2);
        Assert.True(report.SupportsUsb3);
        Assert.True(report.SupportsAlternateModes);
        Assert.True(report.SupportsDualRolePower);
        Assert.True(report.CanProvidePower);
        Assert.True(report.CanConsumePower);
    }
}

public class PowerDataObjectTests
{
    /// <summary>
    /// The real GET_PDOS partner source response with a 65W charger attached. This is the vector
    /// that can be checked against the physical world: the charger is labelled 65W and advertises
    /// the standard 5/9/15/20V profile.
    /// </summary>
    private static readonly byte[] Charger65W =
        Convert.FromHexString("2C91110A2CD112002CB1140045411600");

    [Fact]
    public void SixtyFiveWattChargerDecodesToItsPrintedProfile()
    {
        List<PowerObjectReport> pdos = PowerDataObject.DecodeAll(Charger65W);

        Assert.Equal(4, pdos.Count);
        Assert.All(pdos, p => Assert.Equal("fixed", p.Kind));

        Assert.Equal(5000, pdos[0].VoltageMillivolts);
        Assert.Equal(3000, pdos[0].MaxCurrentMilliamps);

        Assert.Equal(9000, pdos[1].VoltageMillivolts);
        Assert.Equal(15000, pdos[2].VoltageMillivolts);

        Assert.Equal(20000, pdos[3].VoltageMillivolts);
        Assert.Equal(3250, pdos[3].MaxCurrentMilliamps);
        Assert.Equal(65000, pdos[3].MaxPowerMilliwatts);
    }

    [Fact]
    public void HeadlineWattageIsTheHighestOffered()
    {
        List<PowerObjectReport> pdos = PowerDataObject.DecodeAll(Charger65W);
        Assert.Equal(65000, pdos.Max(p => p.MaxPowerMilliwatts));
    }

    [Fact]
    public void ImpossibleFixedSupplyIsReportedAsUnrecognised()
    {
        // Captured from connector 2. Decoded naively this produced a 0V 7.21A power source, which
        // does not exist. Bits 30-31 claiming "fixed" is not enough to make bytes a fixed PDO.
        List<PowerObjectReport> pdos = PowerDataObject.DecodeAll(Convert.FromHexString("D102002C"));

        Assert.Single(pdos);
        Assert.Equal("unrecognised", pdos[0].Kind);
        Assert.Null(pdos[0].VoltageMillivolts);
    }

    [Fact]
    public void AllZeroObjectIsSkippedEntirely()
    {
        Assert.Empty(PowerDataObject.DecodeAll(Convert.FromHexString("00000000")));
    }

    [Fact]
    public void RequestObjectSelectsTheOfferedSupplyByPosition()
    {
        // The real RDO from connector 1: object position 1, operating current 3A.
        List<PowerObjectReport> offered = PowerDataObject.DecodeAll(Charger65W);

        RequestReport? request = PowerDataObject.DecodeRequest(0x1304B12C, offered);

        Assert.NotNull(request);
        Assert.Equal(1, request.ObjectPosition);
        Assert.Equal(3000, request.OperatingCurrentMilliamps);
        Assert.Equal(5000, request.SelectedVoltageMillivolts);
    }

    [Fact]
    public void RequestAgainstAnUnknownPositionDoesNotInventAVoltage()
    {
        RequestReport? request = PowerDataObject.DecodeRequest(0x7004B12C, []);

        Assert.NotNull(request);
        Assert.Null(request.SelectedVoltageMillivolts);
        Assert.Null(request.NegotiatedPowerMilliwatts);
    }
}

public class UcsiProtocolTests
{
    [Fact]
    public void ConnectorCommandMatchesTheDocumentedUcsiControlEncoding()
    {
        // Microsoft documents GET_CONNECTOR_STATUS on connector 1 as 0x00010012. Matching a
        // published example is what validates the CONTROL packing independently of this code.
        Assert.Equal(0x00010012UL,
            UcsiProtocol.ForConnector(UcsiProtocol.CmdGetConnectorStatus, 1));
    }

    [Fact]
    public void AlternateModesCommandUsesAbsoluteBitOffsets()
    {
        // Recipient at 16-18, ConnectorNumber at 24-30, AlternateModeOffset at 32-39,
        // NumberOfAlternateModes at 40-41. This test once asserted the connector at bit 19, which
        // is a reserved field: the controller saw connector zero, answered Error with
        // "non-existent connector number", and that was written up as the command being declined.
        ulong control = UcsiProtocol.GetAlternateModes(recipient: 1, connector: 2, offset: 3, numberMinusOne: 1);

        Assert.Equal(UcsiProtocol.CmdGetAlternateModes, (byte)(control & 0xFF));
        Assert.Equal(1UL, (control >> 16) & 0x07);
        Assert.Equal(0UL, (control >> 19) & 0x1F);
        Assert.Equal(2UL, (control >> 24) & 0x7F);
        Assert.Equal(3UL, (control >> 32) & 0xFF);
        Assert.Equal(1UL, (control >> 40) & 0x03);
    }

    [Fact]
    public void PdosCommandUsesAbsoluteBitOffsets()
    {
        // ConnectorNumber 16-22, PartnerPdo 23, PdoOffset 24-31, NumberOfPdos 32-33,
        // SourceOrSinkPdos 34.
        ulong control = UcsiProtocol.GetPdos(connector: 1, partner: true, offset: 0,
                                             numberMinusOne: 3, source: true);

        Assert.Equal(1UL, (control >> 16) & 0x7F);
        Assert.Equal(1UL, (control >> 23) & 0x01);
        Assert.Equal(3UL, (control >> 32) & 0x03);
        Assert.Equal(1UL, (control >> 34) & 0x01);
    }

    [Fact]
    public void VersionFormatsAsBcd()
    {
        Assert.Equal("1.0", UcsiProtocol.FormatVersion(0x0100));
        Assert.Equal("2.1", UcsiProtocol.FormatVersion(0x0210));
    }
}

public class ErrorStatusTests
{
    [Fact]
    public void NoErrorIsDistinctFromUnrecognisedCommand()
    {
        // This distinction is what separates "the controller has nothing to report" from "the
        // controller does not implement the command".
        Assert.Equal("no error reported", ErrorStatus.Describe([0x00, 0x00]));
        Assert.False(ErrorStatus.IsUnrecognisedCommand([0x00, 0x00]));

        Assert.True(ErrorStatus.IsUnrecognisedCommand([0x01, 0x00]));
        Assert.Contains("unrecognised command", ErrorStatus.Describe([0x01, 0x00]));
    }
}

public class LinkDiagnosticTests
{
    [Fact]
    public void SuperSpeedDeviceOnAHighSpeedLinkIsFlagged()
    {
        // The case people actually hit: a USB 3.x device behind a USB 2.0 cable, running at a
        // twentieth of its capability while Windows says nothing at all.
        Assert.True(Portmark.Core.Usb.LinkDiagnostic.IsUnderperforming(
            bcdUsb: 0x0320, actualSpeed: Portmark.Core.Usb.LinkDiagnostic.SpeedHigh, deviceClass: 0x08));

        string? why = Portmark.Core.Usb.LinkDiagnostic.Explain(0x0320, 2, 0x08);

        Assert.NotNull(why);
        Assert.Contains("480 Mbps", why);
        Assert.Contains("USB 2.0 cable", why);
    }

    [Fact]
    public void DeviceRunningAtItsDeclaredSpeedIsNotFlagged()
    {
        // The camera on the test machine: declares USB 2.01, negotiated High Speed. Correct.
        Assert.False(Portmark.Core.Usb.LinkDiagnostic.IsUnderperforming(0x0201, 2, 0xEF));
        Assert.Null(Portmark.Core.Usb.LinkDiagnostic.Explain(0x0201, 2, 0xEF));
    }

    [Fact]
    public void BillboardDeviceAtLowSpeedIsNeverFlagged()
    {
        // The dock's adapter on the test machine: declares USB 2.01 but attaches at Low Speed,
        // which is what the Billboard class specifies. Flagging it would be a false alarm.
        Assert.False(Portmark.Core.Usb.LinkDiagnostic.IsUnderperforming(0x0201, 0, 0x11));
    }

    [Fact]
    public void HubsAreNotFlaggedSeparatelyFromTheCableFeedingThem()
    {
        // A hub running below its rating is a symptom of its upstream cable, which is reported
        // against that cable rather than counted twice.
        Assert.False(Portmark.Core.Usb.LinkDiagnostic.IsUnderperforming(0x0300, 2, 0x09));
    }

    [Theory]
    [InlineData(0x0320, 3)]   // USB 3.2 expects SuperSpeed
    [InlineData(0x0300, 3)]
    [InlineData(0x0200, 2)]   // USB 2.0 expects High
    [InlineData(0x0110, 1)]   // USB 1.1 expects Full
    public void ExpectedSpeedFollowsTheDeclaredVersion(ushort bcdUsb, byte expected)
    {
        Assert.Equal(expected, Portmark.Core.Usb.LinkDiagnostic.ExpectedSpeed(bcdUsb));
    }
}

public class UsbTopologyTests
{
    private static UsbDeviceReport Device(string vid, string pid, string hubPath,
                                          bool isHub = false, int port = 1) => new()
    {
        VendorId = vid,
        ProductId = pid,
        HubPath = hubPath,
        IsHub = isHub,
        Port = port,
        DeviceClass = isHub ? "Hub" : "Mass Storage",
        Product = isHub ? "Hub" : "Drive",
    };

    [Fact]
    public void DeviceBehindAHubIsNestedUnderIt()
    {
        // The shape actually observed on the test machine: a dock hub on a root port, with the
        // adapter behind it.
        var devices = new List<UsbDeviceReport>
        {
            Device("0x05E3", "0x0608", @"\?\usb#root_hub30#5&aaa", isHub: true),
            Device("0x343C", "0x0000", @"\?\usb#vid_05e3&pid_0608#6&bbb"),
        };

        List<UsbTreeNode> roots = Portmark.Core.Usb.UsbTopology.Build(devices);

        UsbTreeNode root = Assert.Single(roots);
        Assert.Equal("This PC", root.Label);
        UsbTreeNode hub = Assert.Single(root.Children);
        UsbTreeNode behind = Assert.Single(hub.Children);
        Assert.Equal("0x343C", behind.Device!.VendorId);
    }

    [Fact]
    public void IdenticalHubsAreLeftUnnestedRatherThanGuessed()
    {
        // Two hubs with the same vendor and product cannot be told apart by ID alone. Attaching
        // children to whichever matched first would silently invent a topology.
        var devices = new List<UsbDeviceReport>
        {
            Device("0x05E3", "0x0608", @"\?\usb#root_hub30#5&aaa", isHub: true, port: 1),
            Device("0x05E3", "0x0608", @"\?\usb#root_hub30#5&aaa", isHub: true, port: 2),
            Device("0x1111", "0x2222", @"\?\usb#vid_05e3&pid_0608#6&bbb"),
            Device("0x3333", "0x4444", @"\?\usb#vid_05e3&pid_0608#6&ccc"),
        };

        List<UsbTreeNode> roots = Portmark.Core.Usb.UsbTopology.Build(devices);
        List<UsbTreeNode> hubs = roots.SelectMany(r => r.Children).Where(n => n.Device?.IsHub == true).ToList();

        Assert.All(hubs, h => Assert.True(h.AmbiguousTopology));
        Assert.All(hubs, h => Assert.Empty(h.Children));
    }

    [Fact]
    public void DeviceDirectlyOnThePcSitsAtTheTop()
    {
        var devices = new List<UsbDeviceReport>
        {
            Device("0x3938", "0x1191", @"\?\usb#root_hub30#5&aaa"),
        };

        List<UsbTreeNode> roots = Portmark.Core.Usb.UsbTopology.Build(devices);

        Assert.True(Assert.Single(roots).IsRootHub);
        Assert.Single(roots[0].Children);
    }
}

public class PowerBudgetTests
{
    [Fact]
    public void BusPoweredHubHasASharedBudgetLessItsOwnDraw()
    {
        // 500 mA from the upstream port, less 100 mA the hub's own electronics take.
        HubPowerReport r = Portmark.Core.Usb.PowerBudget.Evaluate(
            "hub", isBusPowered: true, controlCurrentMilliamps: 100,
            portCount: 4, requestedMilliamps: 300, deviceCount: 3);

        Assert.Equal(400, r.AvailableMilliamps);
        Assert.False(r.OverSubscribed);
        Assert.Null(r.Note);
    }

    [Fact]
    public void BusPoweredHubAskedForMoreThanItHasIsFlagged()
    {
        HubPowerReport r = Portmark.Core.Usb.PowerBudget.Evaluate(
            "hub", isBusPowered: true, controlCurrentMilliamps: 100,
            portCount: 4, requestedMilliamps: 900, deviceCount: 4);

        Assert.True(r.OverSubscribed);
        Assert.NotNull(r.Note);
        // The wording must not claim a measurement it has not taken.
        Assert.Contains("requested, not what they are drawing", r.Note);
    }

    [Fact]
    public void SelfPoweredHubHasNoSharedPoolAtAll()
    {
        // A self-powered hub has its own supply and a per-port guarantee. Reporting
        // 500 mA x ports would look like a budget without being one.
        HubPowerReport r = Portmark.Core.Usb.PowerBudget.Evaluate(
            "hub", isBusPowered: false, controlCurrentMilliamps: 0,
            portCount: 4, requestedMilliamps: 1900, deviceCount: 4);

        Assert.Null(r.AvailableMilliamps);
        Assert.False(r.OverSubscribed);
        Assert.Equal(500, r.PerPortAllowanceMilliamps);
    }

    [Fact]
    public void BusPoweredPerPortGuaranteeIsLowerThanSelfPowered()
    {
        HubPowerReport bus = Portmark.Core.Usb.PowerBudget.Evaluate("h", true, 0, 4, 0, 0);
        Assert.Equal(100, bus.PerPortAllowanceMilliamps);
    }

    [Fact]
    public void AHungryHubCannotProduceANegativeBudget()
    {
        HubPowerReport r = Portmark.Core.Usb.PowerBudget.Evaluate("h", true, 900, 4, 0, 0);
        Assert.Equal(0, r.AvailableMilliamps);
    }
}

public class UsbWatcherTests
{
    private static UsbDeviceReport Device(string vid, string hub = "hubA", int port = 1) => new()
    {
        VendorId = vid,
        ProductId = "0x0001",
        HubPath = hub,
        Port = port,
        Product = "Thing",
    };

    private static Dictionary<string, UsbDeviceReport> Set(params UsbDeviceReport[] devices)
        => devices.ToDictionary(Portmark.Core.Usb.UsbWatcher.KeyOf);

    [Fact]
    public void ArrivalAndDepartureAreBothReported()
    {
        var before = Set(Device("0x1111"));
        var after = Set(Device("0x2222"));

        List<Portmark.Core.Usb.UsbChange> changes = Portmark.Core.Usb.UsbWatcher.Diff(before, after);

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Kind == Portmark.Core.Usb.UsbChangeKind.Attached
                                   && c.Device.VendorId == "0x2222");
        Assert.Contains(changes, c => c.Kind == Portmark.Core.Usb.UsbChangeKind.Detached
                                   && c.Device.VendorId == "0x1111");
    }

    [Fact]
    public void NoChangeProducesNothing()
    {
        var same = Set(Device("0x1111"), Device("0x2222", port: 2));
        Assert.Empty(Portmark.Core.Usb.UsbWatcher.Diff(same, same));
    }

    [Fact]
    public void MovingADeviceToAnotherPortIsARealChange()
    {
        // Two identical dongles in two ports are two devices, and moving one between ports is
        // something the user did and should be told about. Keying on vendor and product alone
        // would silently swallow both cases.
        var before = Set(Device("0x1111", port: 1));
        var after = Set(Device("0x1111", port: 2));

        List<Portmark.Core.Usb.UsbChange> changes = Portmark.Core.Usb.UsbWatcher.Diff(before, after);

        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void IdenticalDevicesOnDifferentHubsAreDistinct()
    {
        var both = Set(Device("0x1111", hub: "hubA"), Device("0x1111", hub: "hubB"));
        Assert.Equal(2, both.Count);
    }
}
