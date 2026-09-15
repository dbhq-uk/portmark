using Portmark.Core.Model;
using Portmark.Core.Usb;
using Xunit;

namespace Portmark.Core.Tests;

/// <summary>
/// Vectors for the hub's speed report, built from the Microsoft struct definitions in usbioctl.h
/// rather than captured. USB_NODE_CONNECTION_INFORMATION_EX_V2 is four ULONGs: ConnectionIndex,
/// Length, SupportedUsbProtocols (bit 0 Usb110, bit 1 Usb200, bit 2 Usb300) and Flags (bit 0
/// DeviceIsOperatingAtSuperSpeedOrHigher, bit 1 DeviceIsSuperSpeedCapableOrHigher, bit 2
/// DeviceIsOperatingAtSuperSpeedPlusOrHigher, bit 3 DeviceIsSuperSpeedPlusCapableOrHigher).
/// </summary>
public class ConnectionSpeedInfoTests
{
    private static byte[] V2(uint protocols, uint flags)
    {
        byte[] b = new byte[16];
        BitConverter.TryWriteBytes(b.AsSpan(0), 1u);
        BitConverter.TryWriteBytes(b.AsSpan(4), 16u);
        BitConverter.TryWriteBytes(b.AsSpan(8), protocols);
        BitConverter.TryWriteBytes(b.AsSpan(12), flags);
        return b;
    }

    [Fact]
    public void FieldsDecodeFromTheDocumentedBitPositions()
    {
        ConnectionSpeedInfo? info = ConnectionSpeedInfo.Decode(V2(protocols: 0x06, flags: 0x0B));

        Assert.NotNull(info);
        Assert.False(info.PortSupportsUsb110);
        Assert.True(info.PortSupportsUsb200);
        Assert.True(info.PortSupportsUsb300);
        Assert.True(info.OperatingAtSuperSpeedOrHigher);
        Assert.True(info.SuperSpeedCapableOrHigher);
        Assert.False(info.OperatingAtSuperSpeedPlusOrHigher);
        Assert.True(info.SuperSpeedPlusCapableOrHigher);
    }

    [Fact]
    public void ShortResponseDecodesToNothing()
    {
        Assert.Null(ConnectionSpeedInfo.Decode(new byte[12]));
    }
}

/// <summary>
/// BOS vectors laid out from the USB 3.2 specification, section 9.6.2: the BOS header, the USB 2.0
/// Extension, the SuperSpeed USB Device Capability and the SuperSpeedPlus USB Device Capability.
/// </summary>
public class BosSpeedCapabilityTests
{
    internal static readonly byte[] Usb20Extension = [0x07, 0x10, 0x02, 0x06, 0x00, 0x00, 0x00];

    // wSpeedsSupported 0x000E: full, high and 5 Gbps.
    internal static readonly byte[] SuperSpeed = [0x0A, 0x10, 0x03, 0x00, 0x0E, 0x00, 0x01, 0x0A, 0xFF, 0x07];

    // SSAC 1 (two attributes), SSID 0 received and transmitted, lane speed exponent Gb/s,
    // protocol SuperSpeedPlus, mantissa 10.
    internal static readonly byte[] SuperSpeedPlus =
    [
        0x14, 0x10, 0x0A, 0x00,
        0x01, 0x00, 0x00, 0x00,
        0x00, 0x11, 0x00, 0x00,
        0x30, 0x40, 0x0A, 0x00,
        0xB0, 0x40, 0x0A, 0x00,
    ];

    internal static byte[] Bos(params byte[][] capabilities)
    {
        int total = 5 + capabilities.Sum(c => c.Length);
        var bos = new List<byte> { 0x05, 0x0F, (byte)total, (byte)(total >> 8), (byte)capabilities.Length };
        foreach (byte[] c in capabilities) bos.AddRange(c);
        return [.. bos];
    }

    [Fact]
    public void SuperSpeedAndSuperSpeedPlusCapabilitiesAreFound()
    {
        BosSpeedCapability? bos = BosSpeedCapability.Parse(Bos(Usb20Extension, SuperSpeed, SuperSpeedPlus));

        Assert.NotNull(bos);
        Assert.Equal((ushort)0x000E, bos.SpeedsSupported);
        Assert.True(bos.DeclaresHighSpeed);
        Assert.True(bos.DeclaresSuperSpeed);
        Assert.True(bos.DeclaresSuperSpeedPlus);
        Assert.Equal("0A1003000E00010AFF07", bos.SuperSpeedCapabilityHex);
    }

    [Fact]
    public void AUsb2OnlyBosDeclaresNoSpeedsAtAll()
    {
        // A USB 2.0 Extension alone says nothing about speed. Absent is null, not "no".
        BosSpeedCapability? bos = BosSpeedCapability.Parse(Bos(Usb20Extension));

        Assert.NotNull(bos);
        Assert.Null(bos.SpeedsSupported);
        Assert.False(bos.DeclaresSuperSpeed);
        Assert.False(bos.DeclaresSuperSpeedPlus);
    }

    [Fact]
    public void NotABosDescriptorIsNothing()
    {
        Assert.Null(BosSpeedCapability.Parse([0x12, 0x01, 0x00, 0x02, 0x00]));
    }
}

public class LinkDiagnosticTests
{
    private const byte Hid = 0x03;
    private const byte PerInterface = 0x00;

    private static ConnectionSpeedInfo Hub(uint protocols, uint flags) => new(protocols, flags);

    [Fact]
    public void FullSpeedMouseDeclaringUsb2IsNotFlagged()
    {
        // The false alarm this replaced. bcdUSB 2.0 is conformance, not a promise of High Speed: a
        // full-speed-only mouse is a correct USB 2.0 device. The hub reports no higher capability.
        LinkAssessment a = LinkDiagnostic.Assess(
            speed: LinkDiagnostic.SpeedFull, deviceClass: Hid, isHub: false,
            connection: Hub(protocols: 0x07, flags: 0x00), bos: null);

        Assert.False(a.IsUnderperforming);
        Assert.Null(a.Explanation);
        Assert.Null(a.CapableSpeed);
    }

    [Fact]
    public void SuperSpeedCapableDeviceOnAHighSpeedLinkOnAUsb3PortIsFlagged()
    {
        LinkAssessment a = LinkDiagnostic.Assess(
            LinkDiagnostic.SpeedHigh, 0x08, false, Hub(protocols: 0x06, flags: 0x02), null);

        Assert.True(a.IsUnderperforming);
        Assert.Equal("5 Gbps or above", a.CapableSpeed);
        Assert.Contains("480 Mbps", a.Explanation);
        Assert.Contains("supports USB 3", a.Explanation);
        Assert.DoesNotContain("probably both fine", a.Explanation);
    }

    [Fact]
    public void OnAPortWithoutUsb3AndNoCompanionTheExplanationSaysSoInstead()
    {
        // The hub read the connector and reported no companion port sharing it.
        ConnectionSpeedInfo port = Hub(protocols: 0x03, flags: 0x02) with { CompanionPortNumber = 0 };

        LinkAssessment a = LinkDiagnostic.Assess(LinkDiagnostic.SpeedHigh, 0x08, false, port, null);

        Assert.True(a.IsUnderperforming);
        Assert.Contains("does not support USB 3", a.Explanation);
        Assert.DoesNotContain("supports USB 3", a.Explanation);
        Assert.DoesNotContain("probably both fine", a.Explanation);
    }

    [Fact]
    public void AUsb2PortWhoseConnectorIsSharedWithAUsb3CompanionIsNotBlamed()
    {
        // The shape observed live on the test machine: an xHCI root hub lists the USB 2 and USB 3
        // halves of one connector as two port numbers, 1 (protocols 0x3) and 2 (protocols 0x4),
        // each naming the other as its companion. A SuperSpeed device that came up at High Speed
        // sits on the USB 2 number, and saying "this port does not support USB 3" would be false
        // about the socket.
        ConnectionSpeedInfo port = Hub(protocols: 0x03, flags: 0x02)
            with { CompanionPortNumber = 2, CompanionSupportedUsbProtocols = 0x04 };

        LinkAssessment a = LinkDiagnostic.Assess(LinkDiagnostic.SpeedHigh, 0x08, false, port, null);

        Assert.True(a.IsUnderperforming);
        Assert.Contains("companion port 2", a.Explanation);
        Assert.Contains("supports USB 3", a.Explanation);
        Assert.DoesNotContain("cannot run it any faster", a.Explanation);
    }

    [Fact]
    public void WhenTheConnectorCouldNotBeReadTheSocketIsNotJudged()
    {
        // No connector properties: the port number lacks USB 3, but whether a companion shares the
        // socket is unknown, so the port is not declared the limit.
        LinkAssessment a = LinkDiagnostic.Assess(
            LinkDiagnostic.SpeedHigh, 0x08, false, Hub(protocols: 0x03, flags: 0x02), null);

        Assert.True(a.IsUnderperforming);
        Assert.Contains("not known", a.Explanation);
        Assert.DoesNotContain("cannot run it any faster", a.Explanation);
    }

    [Fact]
    public void PortConnectorPropertiesDecodeTheCompanionPort()
    {
        // Laid out from USB_PORT_CONNECTOR_PROPERTIES (ConnectionIndex, ActualLength,
        // UsbPortProperties, CompanionIndex, CompanionPortNumber, then the companion hub's symbolic
        // link as UTF-16) with the values captured live from root hub port 1 on the test machine.
        const string link = "USB#ROOT_HUB30#5&2a4119b5&0&0#{f18a0e88-c30c-11d0-8815-00a0c906bed8}";
        byte[] name = System.Text.Encoding.Unicode.GetBytes(link + "\0");
        byte[] b = new byte[16 + name.Length];
        BitConverter.TryWriteBytes(b.AsSpan(0), 1u);
        BitConverter.TryWriteBytes(b.AsSpan(4), (uint)b.Length);
        BitConverter.TryWriteBytes(b.AsSpan(8), 0x9u);
        BitConverter.TryWriteBytes(b.AsSpan(12), (ushort)0);
        BitConverter.TryWriteBytes(b.AsSpan(14), (ushort)2);
        name.CopyTo(b, 16);

        PortConnectorProperties? p = PortConnectorProperties.Decode(b);

        Assert.NotNull(p);
        Assert.Equal((ushort)2, p.CompanionPortNumber);
        Assert.Equal(link, p.CompanionHubSymbolicLinkName);
        Assert.Null(PortConnectorProperties.Decode(new byte[10]));
    }

    [Fact]
    public void SuperSpeedPlusInOperationIsReportedAsTenGigabitsOrAbove()
    {
        LinkAssessment a = LinkDiagnostic.Assess(
            LinkDiagnostic.SpeedSuper, PerInterface, false, Hub(protocols: 0x04, flags: 0x0F), null);

        Assert.False(a.IsUnderperforming);
        Assert.Contains("10 Gbps or above", a.OperatingSpeed);
        // No Windows field distinguishes 10 from 20 Gbps, so 20 must never appear.
        Assert.DoesNotContain("20", a.OperatingSpeed);
    }

    [Fact]
    public void SuperSpeedWithoutThePlusFlagKeepsTheOpenEndedLabel()
    {
        LinkAssessment a = LinkDiagnostic.Assess(
            LinkDiagnostic.SpeedSuper, PerInterface, false, Hub(protocols: 0x04, flags: 0x03), null);

        Assert.False(a.IsUnderperforming);
        Assert.Equal("SuperSpeed, 5 Gbps or above", a.OperatingSpeed);
    }

    [Fact]
    public void SuperSpeedPlusCapableDeviceRunningAtSuperSpeedIsFlagged()
    {
        // Operating at SuperSpeed, capable of SuperSpeed and SuperSpeedPlus, not operating at Plus.
        LinkAssessment a = LinkDiagnostic.Assess(
            LinkDiagnostic.SpeedSuper, PerInterface, false, Hub(protocols: 0x04, flags: 0x0B), null);

        Assert.True(a.IsUnderperforming);
        Assert.Equal("10 Gbps or above", a.CapableSpeed);
        Assert.DoesNotContain("20", a.Explanation);
    }

    [Fact]
    public void WithNoHubReportTheDevicesOwnBosIsTheEvidence()
    {
        BosSpeedCapability? bos = BosSpeedCapability.Parse(
            BosSpeedCapabilityTests.Bos(BosSpeedCapabilityTests.Usb20Extension, BosSpeedCapabilityTests.SuperSpeed));

        LinkAssessment a = LinkDiagnostic.Assess(LinkDiagnostic.SpeedHigh, 0x08, false, null, bos);

        Assert.True(a.IsUnderperforming);
        Assert.Contains("BOS", a.Explanation);
        Assert.DoesNotContain("supports USB 3", a.Explanation);
    }

    [Fact]
    public void WithNoCapabilityEvidenceNothingIsFlagged()
    {
        // A device declaring USB 3.2 in bcdUSB, at High Speed, with no hub report and no BOS: the
        // version number alone is not evidence of speed.
        LinkAssessment a = LinkDiagnostic.Assess(LinkDiagnostic.SpeedHigh, 0x08, false, null, null);

        Assert.False(a.IsUnderperforming);
        Assert.Null(a.Explanation);
    }

    [Fact]
    public void BillboardDeviceAtLowSpeedIsNeverFlagged()
    {
        // The Billboard class is specified to attach below its capability, so it is never a fault.
        LinkAssessment a = LinkDiagnostic.Assess(
            LinkDiagnostic.SpeedLow, 0x11, false, Hub(protocols: 0x06, flags: 0x02), null);
        Assert.False(a.IsUnderperforming);
    }

    [Fact]
    public void HubsAreNotFlaggedSeparatelyFromTheCableFeedingThem()
    {
        Assert.False(LinkDiagnostic.Assess(
            LinkDiagnostic.SpeedHigh, 0x09, true, Hub(protocols: 0x06, flags: 0x02), null).IsUnderperforming);
    }

    [Fact]
    public void DeviceRunningAtWhatTheHubSaysItCanDoIsNotFlagged()
    {
        // The camera on the test machine: High Speed, and nothing says it can do more.
        LinkAssessment a = LinkDiagnostic.Assess(
            LinkDiagnostic.SpeedHigh, 0xEF, false, Hub(protocols: 0x03, flags: 0x00), null);

        Assert.False(a.IsUnderperforming);
        Assert.Equal("High, 480 Mbps", a.OperatingSpeed);
    }

    [Fact]
    public void TheOperatingFlagOverridesALegacyHighSpeedCode()
    {
        // A hub can leave the legacy Speed at High (2) while its EX_V2 flags say the device is
        // operating at SuperSpeed and capable of it (0x03). Reading the legacy code alone showed
        // 480 Mbps and flagged a device running at exactly what it can do.
        LinkAssessment a = LinkDiagnostic.Assess(
            LinkDiagnostic.SpeedHigh, PerInterface, false, Hub(protocols: 0x07, flags: 0x03), null);

        Assert.False(a.IsUnderperforming);
        Assert.Equal("SuperSpeed, 5 Gbps or above", a.OperatingSpeed);
        Assert.Equal(LinkDiagnostic.SpeedSuper, LinkDiagnostic.OperatingRank(LinkDiagnostic.SpeedHigh, Hub(0x07, 0x03)));
    }

    [Fact]
    public void ALegacyHighSpeedCodeOperatingAtSuperSpeedIsComparedAsSuperSpeed()
    {
        // Operating at SuperSpeed by the flag, capable of SuperSpeedPlus, legacy code still High.
        LinkAssessment a = LinkDiagnostic.Assess(
            LinkDiagnostic.SpeedHigh, PerInterface, false, Hub(protocols: 0x04, flags: 0x0B), null);

        Assert.True(a.IsUnderperforming);
        Assert.Equal("10 Gbps or above", a.CapableSpeed);
        Assert.Contains("at SuperSpeed but not SuperSpeedPlus", a.Explanation);
        Assert.DoesNotContain("480 Mbps", a.Explanation);
    }

    [Fact]
    public void WithoutOperatingFlagsTheLegacyCodeStands()
    {
        Assert.Equal(LinkDiagnostic.SpeedHigh, LinkDiagnostic.OperatingRank(LinkDiagnostic.SpeedHigh, null));
        Assert.Equal(LinkDiagnostic.SpeedHigh, LinkDiagnostic.OperatingRank(LinkDiagnostic.SpeedHigh, Hub(0x07, 0x02)));
    }
}

/// <summary>
/// The watcher polls every second or two, and the link evidence (the hub's EX_V2 report, the
/// connector's companion port and the device's BOS descriptor) does not change while a device stays
/// connected. The cache keeps it per connection and forgets a connection as soon as a scan no
/// longer sees it, so a device that returns is read fresh.
/// </summary>
public class UsbLinkCacheTests
{
    private static UsbConnection Connection(uint port = 1, ushort vid = 0x05AC, ushort pid = 0x12A8, ushort address = 7)
        => new(port, vid, pid, 0, 0, 0, LinkDiagnostic.SpeedSuper, 1, false, address, 0x0320, 0, 0, 0);

    private static UsbLinkReading Reading() => new(new ConnectionSpeedInfo(0x04, 0x03), null);

    [Fact]
    public void AConnectedDeviceIsReadOnceAcrossScans()
    {
        var cache = new UsbLinkCache();
        string key = UsbLinkCache.KeyOf("hubA", Connection());
        int reads = 0;

        for (int scan = 0; scan < 3; scan++)
        {
            cache.GetOrRead(key, () => { reads++; return Reading(); });
            cache.EndScan();
        }

        Assert.Equal(1, reads);
    }

    [Fact]
    public void ADeviceMissingFromAScanIsReadAgainWhenItReturns()
    {
        var cache = new UsbLinkCache();
        string key = UsbLinkCache.KeyOf("hubA", Connection());
        int reads = 0;

        cache.GetOrRead(key, () => { reads++; return Reading(); });
        cache.EndScan();
        cache.EndScan();   // a scan in which the device was not seen
        cache.GetOrRead(key, () => { reads++; return Reading(); });

        Assert.Equal(2, reads);
    }

    [Fact]
    public void AReEnumeratedDeviceIsADifferentConnection()
    {
        // Same socket, same IDs, new address: Windows enumerated it again, so the link may differ.
        Assert.NotEqual(UsbLinkCache.KeyOf("hubA", Connection(address: 7)),
                        UsbLinkCache.KeyOf("hubA", Connection(address: 8)));
        Assert.NotEqual(UsbLinkCache.KeyOf("hubA", Connection(port: 1)),
                        UsbLinkCache.KeyOf("hubA", Connection(port: 2)));
        Assert.NotEqual(UsbLinkCache.KeyOf("hubA", Connection()),
                        UsbLinkCache.KeyOf("hubB", Connection()));
        Assert.NotEqual(UsbLinkCache.KeyOf("hubA", Connection(pid: 1)),
                        UsbLinkCache.KeyOf("hubA", Connection(pid: 2)));
    }
}

/// <summary>
/// USB_NODE_CONNECTION_INFORMATION_EX laid out from usbioctl.h: ConnectionIndex (4), the packed
/// 18-byte USB_DEVICE_DESCRIPTOR, then CurrentConfigurationValue, Speed, DeviceIsHub,
/// DeviceAddress (2), NumberOfOpenPipes (4) and ConnectionStatus (4) at offset 31.
/// USB_CONNECTION_STATUS numbers its members from NoDeviceConnected = 0.
/// </summary>
public class UsbConnectionStatusTests
{
    private static byte[] Info(uint port, uint status, ushort? vid = null)
    {
        byte[] b = new byte[35];
        BitConverter.TryWriteBytes(b.AsSpan(0), port);
        if (vid is ushort v)
        {
            b[4] = 0x12;
            b[5] = 0x01;
            BitConverter.TryWriteBytes(b.AsSpan(12), v);
        }
        BitConverter.TryWriteBytes(b.AsSpan(31), status);
        return b;
    }

    [Fact]
    public void FailedEnumerationWithNoDescriptorIsAFaultWithNoIdentity()
    {
        UsbPortStatusReport r = UsbConnectionStatus.Decode(Info(3, 2), "hub", 3);

        Assert.Equal(3, r.Port);
        Assert.Equal(2, r.ConnectionStatusCode);
        Assert.Equal("DeviceFailedEnumeration", r.ConnectionStatus);
        Assert.True(r.IsFault);
        Assert.StartsWith("the hub reports", r.Description);
        // An all-zero descriptor is the hardware having nothing to say, not vendor 0x0000.
        Assert.Null(r.VendorId);
        Assert.Null(r.ProductId);
    }

    [Fact]
    public void AValidDescriptorOnAFaultedPortIsKept()
    {
        UsbPortStatusReport r = UsbConnectionStatus.Decode(Info(1, 5, vid: 0x1234), "hub", 1);

        Assert.Equal("DeviceNotEnoughPower", r.ConnectionStatus);
        Assert.Equal("0x1234", r.VendorId);
    }

    [Fact]
    public void AFaultedPortCarriesTheRegisteredNameOnlyWhenItHasAVendorId()
    {
        UsbPortStatusReport named = UsbConnectionStatus.Decode(Info(1, 5, vid: 0x05AC), "hub", 1);
        UsbPortStatusReport bare = UsbConnectionStatus.Decode(Info(1, 2), "hub", 1);

        Assert.Equal("Apple, Inc.", named.VendorName);
        Assert.Null(bare.VendorName);

        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(named, options));
        Assert.Equal("Apple, Inc.", json.RootElement.GetProperty("vendorName").GetString());
    }

    [Theory]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    [InlineData(5u, true)]
    [InlineData(6u, true)]
    [InlineData(7u, true)]
    [InlineData(8u, true)]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(9u, false)]    // enumerating: in progress, not a failure
    [InlineData(10u, false)]   // reset: in progress, not a failure
    [InlineData(99u, false)]   // undocumented: reported, never promoted to a fault
    public void OnlyTheDocumentedFailureStatusesAreFaults(uint status, bool fault)
    {
        Assert.Equal(fault, UsbConnectionStatus.IsFault(status));
    }

    [Fact]
    public void DescriptionsAttributeTheStatusToTheHubAndClaimNoCause()
    {
        for (uint s = 2; s <= 10; s++)
        {
            string d = UsbConnectionStatus.Describe(s);
            Assert.StartsWith("the hub reports", d);
            Assert.DoesNotContain("because", d);
            Assert.DoesNotContain("cable", d);
        }
    }
}

public class UsbPortFaultTrackerTests
{
    private static UsbPortStatusReport Port(uint status, int port = 1, string hub = "hubA") => new()
    {
        HubPath = hub,
        Port = port,
        ConnectionStatusCode = (int)status,
        ConnectionStatus = UsbConnectionStatus.Name(status),
        IsFault = UsbConnectionStatus.IsFault(status),
        Description = UsbConnectionStatus.Describe(status),
    };

    [Fact]
    public void AFaultIsReportedOnceWhileItPersists()
    {
        var tracker = new UsbPortFaultTracker();
        tracker.Prime([]);

        Assert.Single(tracker.Update([Port(2)]));
        Assert.Empty(tracker.Update([Port(2)]));
        Assert.Empty(tracker.Update([Port(2)]));
    }

    [Fact]
    public void FaultsAlreadyPresentWhenWatchingStartsAreNotNewOccurrences()
    {
        var tracker = new UsbPortFaultTracker();
        tracker.Prime([Port(4)]);

        Assert.Empty(tracker.Update([Port(4)]));
    }

    [Fact]
    public void ARetryThroughEnumeratingIsTheSameOccurrence()
    {
        var tracker = new UsbPortFaultTracker();
        tracker.Prime([]);

        Assert.Single(tracker.Update([Port(2)]));
        Assert.Empty(tracker.Update([Port(9)]));
        Assert.Empty(tracker.Update([Port(2)]));
    }

    [Fact]
    public void ClearingAndReturningIsASecondOccurrence()
    {
        var tracker = new UsbPortFaultTracker();
        tracker.Prime([]);

        Assert.Single(tracker.Update([Port(2)]));
        Assert.Empty(tracker.Update([]));
        Assert.Single(tracker.Update([Port(2)]));
    }

    [Fact]
    public void ADifferentFaultOnTheSamePortIsReported()
    {
        var tracker = new UsbPortFaultTracker();
        tracker.Prime([]);

        tracker.Update([Port(2)]);
        UsbPortStatusReport r = Assert.Single(tracker.Update([Port(4)]));
        Assert.Equal("DeviceCausedOvercurrent", r.ConnectionStatus);
    }

    [Fact]
    public void TransientStatusesAloneAreNeverReported()
    {
        var tracker = new UsbPortFaultTracker();
        tracker.Prime([]);

        Assert.Empty(tracker.Update([Port(9), Port(10, port: 2)]));
    }
}

public class UsbTopologyPortStatusTests
{
    [Fact]
    public void AFaultedPortWithNoDeviceAppearsUnderItsHub()
    {
        const string root = @"\?\usb#root_hub30#5&aaa";
        var ports = new List<UsbPortStatusReport>
        {
            new()
            {
                HubPath = root, Port = 3, ConnectionStatusCode = 2,
                ConnectionStatus = "DeviceFailedEnumeration", IsFault = true,
                Description = UsbConnectionStatus.Describe(2),
            },
        };

        List<UsbTreeNode> roots = UsbTopology.Build([], ports);

        UsbTreeNode hub = Assert.Single(roots);
        UsbTreeNode port = Assert.Single(hub.Children);
        Assert.Null(port.Device);
        Assert.NotNull(port.PortStatus);
        Assert.Contains("Port 3", port.Label);
    }
}

/// <summary>
/// Billboard capability vectors laid out from the USB Billboard Device Class specification: a
/// 44-byte fixed part (bLength, bDescriptorType, bDevCapabilityType, iAdditionalInfoURL,
/// bNumberOfAlternateOrUSB4Modes, bPreferredAlternateOrUSB4Mode, VCONNPower (2), bmConfigured
/// (32), bcdVersion (2), bAdditionalFailureInfo, bReserved) then four bytes per mode.
/// </summary>
public class BillboardParseTests
{
    private static byte[] Capability(int declaredModes, params (ushort Svid, byte Mode)[] modes)
    {
        var cap = new List<byte>
        {
            (byte)(44 + (declaredModes * 4)), 0x10, 0x0D, 0x00, (byte)declaredModes, 0x00, 0x00, 0x00,
        };
        byte[] configured = new byte[32];
        configured[0] = 0x07;   // mode 0: 11b entered, mode 1: 01b not attempted or exited
        cap.AddRange(configured);
        cap.AddRange([0x21, 0x01, 0x00, 0x00]);
        foreach ((ushort svid, byte mode) in modes)
            cap.AddRange([(byte)svid, (byte)(svid >> 8), mode, 0x00]);
        return [.. cap];
    }

    [Fact]
    public void StateOneIsNotAttemptedOrExited()
    {
        byte[] bos = BosSpeedCapabilityTests.Bos(Capability(2, (0xFF01, 0), (0x8087, 1)));

        BillboardReport? r = BillboardReader.ParseBillboard(bos, 0x1234, 0x5678);

        Assert.NotNull(r);
        Assert.False(r.Truncated);
        Assert.Equal(2, r.Modes.Count);
        Assert.Equal("entered successfully", r.Modes[0].State);
        Assert.Equal("not attempted or exited", r.Modes[1].State);
    }

    [Fact]
    public void ModesCutShortAreReportedAsTruncated()
    {
        // The capability declares two modes, but the bytes stop after the first.
        byte[] full = BosSpeedCapabilityTests.Bos(Capability(2, (0xFF01, 0), (0x8087, 1)));
        byte[] cut = full[..(5 + 44 + 4)];

        BillboardReport? r = BillboardReader.ParseBillboard(cut, 0x1234, 0x5678);

        Assert.NotNull(r);
        Assert.True(r.Truncated);
        Assert.Single(r.Modes);
        Assert.NotNull(r.TruncationNote);
    }

    [Fact]
    public void AFixedPartCutShortIsTruncatedRatherThanDropped()
    {
        byte[] full = BosSpeedCapabilityTests.Bos(Capability(1, (0xFF01, 0)));
        byte[] cut = full[..(5 + 30)];

        BillboardReport? r = BillboardReader.ParseBillboard(cut, 0x1234, 0x5678);

        Assert.NotNull(r);
        Assert.True(r.Truncated);
        Assert.Empty(r.Modes);
    }

    [Fact]
    public void ABLengthTooShortForTheDeclaredModesIsTruncated()
    {
        byte[] cap = Capability(2, (0xFF01, 0), (0x8087, 1));
        cap[0] = 44 + 4;   // bLength admits one mode while bNumberOfAlternateOrUSB4Modes says two
        byte[] bos = BosSpeedCapabilityTests.Bos(cap[..(44 + 4)]);

        BillboardReport? r = BillboardReader.ParseBillboard(bos, 0x1234, 0x5678);

        Assert.NotNull(r);
        Assert.True(r.Truncated);
        Assert.Single(r.Modes);
    }
}
