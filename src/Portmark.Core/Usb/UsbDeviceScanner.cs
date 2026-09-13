using Microsoft.Win32.SafeHandles;
using Portmark.Core.Model;

namespace Portmark.Core.Usb;

/// <summary>
/// Walks every USB hub port and describes what is attached, reading the device's own descriptors
/// rather than repeating what Windows has cached in PnP. Needs no elevation and no setup.
/// </summary>
public static class UsbDeviceScanner
{
    public static List<UsbDeviceReport> ScanAll()
    {
        var devices = new List<UsbDeviceReport>();

        foreach (string hubPath in UsbHubIo.EnumerateHubs())
        {
            SafeFileHandle hub = UsbHubIo.OpenHub(hubPath);
            if (hub.IsInvalid) { hub.Dispose(); continue; }

            using (hub)
            {
                int ports = UsbHubIo.GetPortCount(hub);
                for (uint port = 1; port <= ports; port++)
                {
                    UsbConnection? c = UsbHubIo.GetConnection(hub, port);
                    if (c is null) continue;
                    devices.Add(Describe(hub, hubPath, c));
                }
            }
        }

        return devices;
    }

    private static UsbDeviceReport Describe(SafeFileHandle hub, string hubPath, UsbConnection c)
    {
        return new UsbDeviceReport
        {
            VendorId = $"0x{c.VendorId:X4}",
            ProductId = $"0x{c.ProductId:X4}",
            Manufacturer = UsbHubIo.GetString(hub, c.Port, c.ManufacturerStringIndex),
            Product = UsbHubIo.GetString(hub, c.Port, c.ProductStringIndex),
            SerialNumber = UsbHubIo.GetString(hub, c.Port, c.SerialStringIndex),
            DeviceClass = UsbHubIo.DeviceClassName(c.DeviceClass),
            DeviceClassCode = c.DeviceClass,
            Speed = UsbHubIo.SpeedName(c.Speed),
            SpeedCode = c.Speed,
            UsbVersion = $"{(c.UsbVersion >> 8) & 0xFF:X}.{(c.UsbVersion >> 4) & 0x0F:X}",
            MaxPowerMilliamps = UsbHubIo.GetMaxPowerMilliamps(hub, c.Port, c.Speed),
            IsHub = c.IsHub,
            Address = c.Address,
            Port = (int)c.Port,
            HubPath = hubPath,
        };
    }
}
