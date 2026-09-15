using Microsoft.Win32.SafeHandles;
using Portmark.Core.Model;

namespace Portmark.Core.Usb;

/// <summary>
/// Walks every USB hub port and describes what is attached, reading the device's own descriptors
/// rather than repeating what Windows has cached in PnP. Needs no elevation and no setup.
/// </summary>
public static class UsbDeviceScanner
{
    public static List<UsbDeviceReport> ScanAll() => Scan().Devices;

    /// <summary>
    /// The attached devices, and every port whose hub reports a status other than empty or
    /// connected. A port whose device failed enumeration has no device to describe, and dropping
    /// it with the empty ports hid the one port the user most needed to hear about.
    /// </summary>
    public static UsbScanReport Scan()
    {
        var scan = new UsbScanReport();

        foreach (string hubPath in UsbHubIo.EnumerateHubs())
        {
            SafeFileHandle hub = UsbHubIo.OpenHub(hubPath);
            if (hub.IsInvalid) { hub.Dispose(); continue; }

            using (hub)
            {
                int ports = UsbHubIo.GetPortCount(hub);
                for (uint port = 1; port <= ports; port++)
                {
                    byte[]? info = UsbHubIo.GetConnectionInformation(hub, port);
                    if (info is null) continue;

                    UsbConnection? c = UsbHubIo.ParseConnection(port, info);
                    if (c is not null)
                    {
                        scan.Devices.Add(Describe(hub, hubPath, c));
                        continue;
                    }

                    UsbPortStatusReport status = UsbConnectionStatus.Decode(info, hubPath, port);
                    if (status.ConnectionStatusCode != UsbConnectionStatus.NoDeviceConnected)
                        scan.PortStatuses.Add(status);
                }
            }
        }

        return scan;
    }

    private static UsbDeviceReport Describe(SafeFileHandle hub, string hubPath, UsbConnection c)
    {
        ConnectionSpeedInfo? link = WithCompanion(hub, hubPath, c.Port, UsbHubIo.GetConnectionSpeedInfo(hub, c.Port));
        BosSpeedCapability? bos = UsbHubIo.GetBosSpeedCapability(hub, c.Port, c.UsbVersion);
        LinkAssessment assessment = LinkDiagnostic.Assess(c.Speed, c.DeviceClass, c.IsHub, link, bos);

        return new UsbDeviceReport
        {
            VendorId = $"0x{c.VendorId:X4}",
            ProductId = $"0x{c.ProductId:X4}",
            Manufacturer = UsbHubIo.GetString(hub, c.Port, c.ManufacturerStringIndex),
            Product = UsbHubIo.GetString(hub, c.Port, c.ProductStringIndex),
            SerialNumber = UsbHubIo.GetString(hub, c.Port, c.SerialStringIndex),
            DeviceClass = UsbHubIo.DeviceClassName(c.DeviceClass),
            DeviceClassCode = c.DeviceClass,
            Speed = assessment.OperatingSpeed,
            SpeedCode = c.Speed,
            UsbVersion = $"{(c.UsbVersion >> 8) & 0xFF:X}.{(c.UsbVersion >> 4) & 0x0F:X}",
            MaxPowerMilliamps = UsbHubIo.GetMaxPowerMilliamps(hub, c.Port, c.Speed),
            IsHub = c.IsHub,
            Address = c.Address,
            Port = (int)c.Port,
            HubPath = hubPath,
            IsUnderperforming = assessment.IsUnderperforming,
            LinkDiagnosis = assessment.Explanation,
            ExpectedSpeed = assessment.CapableSpeed,
            LinkEvidence = Evidence(link, bos, c.UsbVersion),
        };
    }

    /// <summary>
    /// Adds the port that shares this port's connector, and that port's protocols. On an xHCI root
    /// hub the companion is on the same hub, so the open handle is reused; otherwise the companion
    /// hub is opened by the symbolic link the hub gave. Anything unreadable stays null.
    /// </summary>
    private static ConnectionSpeedInfo? WithCompanion(SafeFileHandle hub, string hubPath, uint port,
                                                      ConnectionSpeedInfo? link)
    {
        if (link is null) return null;

        PortConnectorProperties? connector = UsbHubIo.GetPortConnectorProperties(hub, port);
        if (connector is null) return link;
        if (connector.CompanionPortNumber == 0) return link with { CompanionPortNumber = 0 };

        uint? protocols = null;
        if (connector.CompanionHubSymbolicLinkName is { } name)
        {
            string companionPath = name.StartsWith(@"\\", StringComparison.Ordinal) ? name : @"\\?\" + name;

            if (string.Equals(companionPath, hubPath, StringComparison.OrdinalIgnoreCase))
            {
                protocols = UsbHubIo.GetConnectionSpeedInfo(hub, connector.CompanionPortNumber)?.SupportedUsbProtocols;
            }
            else
            {
                using SafeFileHandle companion = UsbHubIo.OpenHub(companionPath);
                if (!companion.IsInvalid)
                    protocols = UsbHubIo.GetConnectionSpeedInfo(companion, connector.CompanionPortNumber)?.SupportedUsbProtocols;
            }
        }

        // All-zero protocols is the hub having nothing to say about the companion, not a port
        // that supports nothing.
        if (protocols is uint p && (p & 0x7) == 0) protocols = null;

        return link with { CompanionPortNumber = connector.CompanionPortNumber, CompanionSupportedUsbProtocols = protocols };
    }

    /// <summary>The readings the diagnosis rests on, raw alongside decoded, each null with a reason.</summary>
    private static UsbLinkEvidenceReport Evidence(ConnectionSpeedInfo? link, BosSpeedCapability? bos, ushort bcdUsb) => new()
    {
        SupportedUsbProtocolsHex = link is null ? null : $"0x{link.SupportedUsbProtocols:X8}",
        PortProtocols = link?.ProtocolList(),
        PortSupportsUsb110 = link?.PortSupportsUsb110,
        PortSupportsUsb200 = link?.PortSupportsUsb200,
        PortSupportsUsb300 = link?.PortSupportsUsb300,
        FlagsHex = link is null ? null : $"0x{link.Flags:X8}",
        OperatingAtSuperSpeedOrHigher = link?.OperatingAtSuperSpeedOrHigher,
        SuperSpeedCapableOrHigher = link?.SuperSpeedCapableOrHigher,
        OperatingAtSuperSpeedPlusOrHigher = link?.OperatingAtSuperSpeedPlusOrHigher,
        SuperSpeedPlusCapableOrHigher = link?.SuperSpeedPlusCapableOrHigher,
        CompanionPortNumber = link?.CompanionPortNumber,
        CompanionSupportedUsbProtocolsHex = link?.CompanionSupportedUsbProtocols is uint cp ? $"0x{cp:X8}" : null,
        CompanionPortProtocols = link?.CompanionSupportedUsbProtocols is uint cq
            ? new ConnectionSpeedInfo(cq, 0).ProtocolList()
            : null,
        HubReportReason = link is null
            ? "The hub did not answer IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 for this port."
            : null,
        BosSpeedsSupportedHex = bos?.SpeedsSupported is ushort s ? $"0x{s:X4}" : null,
        BosSuperSpeedCapabilityHex = bos?.SuperSpeedCapabilityHex,
        BosSuperSpeedPlusCapabilityHex = bos?.SuperSpeedPlusCapabilityHex,
        BosReason = bcdUsb < 0x0201
            ? "The device declares a USB version below 2.01, which predates the BOS descriptor."
            : bos is null
                ? "The device returned no BOS descriptor."
                : bos.SuperSpeedCapabilityHex is null && bos.SuperSpeedPlusCapabilityHex is null
                    ? "The device's BOS descriptor has no SuperSpeed or SuperSpeedPlus capability."
                    : null,
    };
}
