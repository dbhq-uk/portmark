using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UcsiProbe.Native;

namespace UcsiProbe.Probes;

/// <summary>
/// Fallback data source. IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX reports the negotiated
/// link speed and device descriptors of whatever is attached. It says nothing about the cable:
/// no e-marker, no power contract, no video capability. Included so the spike can state exactly
/// how much is still obtainable if the UCSI path is closed.
/// </summary>
internal static class UsbHubProbe
{
    private const uint FILE_DEVICE_USB = 0x22;
    private static readonly uint IOCTL_USB_GET_NODE_INFORMATION = Win32.CtlCode(FILE_DEVICE_USB, 258, 0, 0);
    private static readonly uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = Win32.CtlCode(FILE_DEVICE_USB, 274, 0, 0);

    private static string SpeedName(byte speed) => speed switch
    {
        0 => "Low (1.5 Mbps)",
        1 => "Full (12 Mbps)",
        2 => "High (480 Mbps)",
        3 => "Super (5 Gbps or above)",
        _ => $"unknown ({speed})",
    };

    internal static void Run(Report report, string stage)
    {
        List<string> hubs = Devices.EnumerateInterfacePaths(Ucsi.GUID_DEVINTERFACE_USB_HUB, out int enumErr);
        report.Add(new Attempt
        {
            Stage = stage,
            Api = "SetupDiEnumDeviceInterfaces",
            Target = "GUID_DEVINTERFACE_USB_HUB",
            Success = hubs.Count > 0,
            Win32Error = enumErr,
            Result = $"{hubs.Count} hub interfaces",
        });

        int devicesFound = 0;
        foreach (string hub in hubs)
            devicesFound += ProbeHub(report, stage, hub);

        report.Notes.Add(devicesFound > 0
            ? $"USB hub fallback: enumerated {devicesFound} attached device(s) with negotiated link speed. This is not cable data."
            : "USB hub fallback: no attached devices reported.");
    }

    private static unsafe int ProbeHub(Report report, string stage, string hubPath)
    {
        SafeFileHandle h = Win32.CreateFile(
            hubPath, Win32.GENERIC_WRITE, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero,
            Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();

        if (h.IsInvalid)
        {
            report.Add(new Attempt
            {
                Stage = stage, Api = "CreateFileW", Target = hubPath, Detail = "USB hub",
                Success = false, Win32Error = err, Error = Win32.Describe(err),
            });
            h.Dispose();
            return 0;
        }

        using (h)
        {
            // USB_NODE_INFORMATION: NodeType (4 bytes), then USB_HUB_DESCRIPTOR whose
            // bNumberOfPorts sits at offset 2 within the descriptor, so offset 6 overall.
            byte[] node = new byte[128];
            int ports;
            fixed (byte* p = node)
            {
                if (!Win32.DeviceIoControl(h, IOCTL_USB_GET_NODE_INFORMATION, p, (uint)node.Length,
                                           p, (uint)node.Length, out _, IntPtr.Zero))
                {
                    int e = Marshal.GetLastWin32Error();
                    report.Add(new Attempt
                    {
                        Stage = stage, Api = "DeviceIoControl", Target = hubPath,
                        Detail = "IOCTL_USB_GET_NODE_INFORMATION", Success = false,
                        Win32Error = e, Error = Win32.Describe(e),
                    });
                    return 0;
                }
                ports = node[6];
            }

            int found = 0;
            for (uint port = 1; port <= ports; port++)
            {
                byte[] buf = new byte[512];
                BitConverter.TryWriteBytes(buf, port);
                bool ok;
                fixed (byte* p = buf)
                {
                    ok = Win32.DeviceIoControl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX,
                                               p, (uint)buf.Length, p, (uint)buf.Length, out _, IntPtr.Zero);
                }
                if (!ok) continue;

                ushort vid = BitConverter.ToUInt16(buf, 4 + 8);
                ushort pid = BitConverter.ToUInt16(buf, 4 + 10);
                byte speed = buf[23];
                uint connectionStatus = BitConverter.ToUInt32(buf, 32);
                if (connectionStatus != 1) continue;   // 1 == DeviceConnected

                found++;
                report.Add(new Attempt
                {
                    Stage = stage, Api = "DeviceIoControl", Target = hubPath,
                    Detail = $"IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX port {port}",
                    Success = true,
                    Result = $"VID_{vid:X4}&PID_{pid:X4} negotiated speed {SpeedName(speed)}",
                });
            }
            return found;
        }
    }
}
