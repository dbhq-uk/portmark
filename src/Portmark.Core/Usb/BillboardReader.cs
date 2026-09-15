using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Portmark.Core.Model;
using Portmark.Core.Native;

namespace Portmark.Core.Usb;

/// <summary>
/// Reads USB Billboard capability descriptors.
///
/// A USB-C adapter that supports an Alternate Mode exposes a Billboard device whose BOS descriptor
/// declares every Alternate Mode it supports, by SVID, together with whether each one was actually
/// entered. SVID 0xFF01 is DisplayPort. That answers the video question directly, and it answers
/// it with evidence rather than inference from the shape of a connector.
///
/// This path matters strategically: it uses documented USB hub IOCTLs, so it needs no UCSI test
/// interface, no registry change and no administrator rights. Where UCSI declined to identify
/// alternate modes, this succeeds on an ordinary user account.
///
/// Sources: USB Billboard Device Class specification, and the USB_DESCRIPTOR_REQUEST /
/// IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION reference on Microsoft Learn.
/// </summary>
public static class BillboardReader
{
    private const uint FileDeviceUsb = 0x22;
    private static readonly uint IoctlGetNodeInformation = Win32.CtlCode(FileDeviceUsb, 258, 0, 0);
    private static readonly uint IoctlGetDescriptorFromNodeConnection = Win32.CtlCode(FileDeviceUsb, 260, 0, 0);
    private static readonly uint IoctlGetNodeConnectionInformationEx = Win32.CtlCode(FileDeviceUsb, 274, 0, 0);

    private static readonly Guid UsbHubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    private const byte DescriptorTypeBos = 0x0F;
    private const byte DescriptorTypeDeviceCapability = 0x10;
    private const byte CapabilityTypeBillboard = 0x0D;

    /// <summary>DisplayPort Alternate Mode, as assigned by VESA.</summary>
    public const ushort SvidDisplayPort = 0xFF01;

    /// <summary>USB device class 0x11 is the Billboard Device Class.</summary>
    public const byte BillboardDeviceClass = 0x11;

    /// <summary>Diagnostic trace of the scan, populated when tracing is enabled.</summary>
    public static List<string> Trace { get; } = [];
    public static bool Tracing { get; set; }

    /// <summary>Scans every USB hub port and returns each Billboard device found.</summary>
    public static List<BillboardReport> FindAll()
    {
        var found = new List<BillboardReport>();
        Trace.Clear();

        IReadOnlyList<string> hubs = DeviceInterfaces.FindAll(UsbHubInterface);
        if (Tracing) Trace.Add($"{hubs.Count} hub interface(s)");

        foreach (string hubPath in hubs)
        {
            SafeFileHandle hub = Win32.CreateFile(
                hubPath, Win32.GENERIC_WRITE, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
                IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (hub.IsInvalid)
            {
                if (Tracing) Trace.Add($"  open FAILED {Win32.Describe(Marshal.GetLastWin32Error())} {hubPath}");
                hub.Dispose();
                continue;
            }

            using (hub)
            {
                int ports = GetPortCount(hub);
                if (Tracing) Trace.Add($"  hub {Short(hubPath)}: {ports} port(s)");

                for (uint port = 1; port <= ports; port++)
                {
                    if (!TryGetConnection(hub, port, out ushort vid, out ushort pid)) continue;
                    if (Tracing) Trace.Add($"    port {port}: VID_{vid:X4}&PID_{pid:X4}");

                    byte[]? header = GetDescriptor(hub, port, DescriptorTypeBos, 5);
                    if (header is null)
                    {
                        if (Tracing) Trace.Add($"      no BOS descriptor returned");
                        continue;
                    }
                    if (Tracing) Trace.Add($"      BOS header {Convert.ToHexString(header)}");

                    BillboardReport? report = ReadBillboard(hub, port, vid, pid);
                    if (report is not null) found.Add(report);
                    else if (Tracing) Trace.Add($"      no billboard capability in BOS");
                }
            }
        }

        return found;
    }

    private static string Short(string path)
    {
        int i = path.IndexOf("#{", StringComparison.Ordinal);
        return i > 0 ? path[..i] : path;
    }

    private static unsafe int GetPortCount(SafeFileHandle hub)
    {
        byte[] node = new byte[128];
        fixed (byte* p = node)
        {
            if (!Win32.DeviceIoControl(hub, IoctlGetNodeInformation, p, (uint)node.Length,
                                       p, (uint)node.Length, out _, IntPtr.Zero))
                return 0;
        }
        // USB_NODE_INFORMATION: NodeType (4 bytes), then USB_HUB_DESCRIPTOR whose bNumberOfPorts
        // is at offset 2 within the descriptor, so offset 6 overall.
        return node[6];
    }

    private static unsafe bool TryGetConnection(SafeFileHandle hub, uint port, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        byte[] buffer = new byte[512];
        BitConverter.TryWriteBytes(buffer, port);

        bool ok;
        int err;
        uint returned;
        fixed (byte* p = buffer)
        {
            ok = Win32.DeviceIoControl(hub, IoctlGetNodeConnectionInformationEx,
                                       p, (uint)buffer.Length, p, (uint)buffer.Length,
                                       out returned, IntPtr.Zero);
            err = Marshal.GetLastWin32Error();
        }

        if (!ok)
        {
            if (Tracing) Trace.Add($"    port {port}: IOCTL failed {Win32.Describe(err)}");
            return false;
        }

        // USB_NODE_CONNECTION_INFORMATION_EX is byte-packed, because the USB_DEVICE_DESCRIPTOR it
        // embeds is declared with pshpack1. So the fields after it are not naturally aligned:
        //   0      ConnectionIndex (4)
        //   4-21   DeviceDescriptor (18), with idVendor at +8 and idProduct at +10
        //   22     CurrentConfigurationValue     23  Speed        24  DeviceIsHub
        //   25-26  DeviceAddress                 27-30  NumberOfOpenPipes
        //   31-34  ConnectionStatus
        // Reading ConnectionStatus at 32 instead of 31 made every port look disconnected.
        uint status = BitConverter.ToUInt32(buffer, 31);
        byte deviceClass = buffer[4 + 4];

        if (Tracing)
            Trace.Add($"    port {port}: VID_{BitConverter.ToUInt16(buffer, 12):X4}"
                    + $"&PID_{BitConverter.ToUInt16(buffer, 14):X4} "
                    + $"class=0x{deviceClass:X2} status={status}");

        if (status != 1) return false;   // 1 == DeviceConnected

        vid = BitConverter.ToUInt16(buffer, 4 + 8);
        pid = BitConverter.ToUInt16(buffer, 4 + 10);
        return true;
    }

    /// <summary>Requests the BOS descriptor and parses any Billboard capability inside it.</summary>
    private static BillboardReport? ReadBillboard(SafeFileHandle hub, uint port, ushort vid, ushort pid)
    {
        // Read the 5-byte BOS header first to learn the full length, then read the whole thing.
        byte[]? header = GetDescriptor(hub, port, DescriptorTypeBos, 5);
        if (header is null || header.Length < 5 || header[1] != DescriptorTypeBos) return null;

        ushort totalLength = BitConverter.ToUInt16(header, 2);
        if (totalLength <= 5 || totalLength > 4096) return null;

        byte[]? bos = GetDescriptor(hub, port, DescriptorTypeBos, totalLength);
        if (bos is null || bos.Length < totalLength) return null;

        return ParseBillboard(bos, vid, pid);
    }

    private static unsafe byte[]? GetDescriptor(SafeFileHandle hub, uint port, byte type, int length)
    {
        // USB_DESCRIPTOR_REQUEST: ConnectionIndex(4) then an 8-byte setup packet, then the data.
        const int headerSize = 12;
        byte[] buffer = new byte[headerSize + length];

        BitConverter.TryWriteBytes(buffer.AsSpan(0), port);
        buffer[4] = 0x80;                                          // bmRequest: device to host
        buffer[5] = 0x06;                                          // bRequest: GET_DESCRIPTOR
        BitConverter.TryWriteBytes(buffer.AsSpan(6), (ushort)(type << 8));   // wValue
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);             // wIndex
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)length);       // wLength

        uint returned;
        bool ok;
        fixed (byte* p = buffer)
        {
            ok = Win32.DeviceIoControl(hub, IoctlGetDescriptorFromNodeConnection,
                                       p, (uint)buffer.Length, p, (uint)buffer.Length,
                                       out returned, IntPtr.Zero);
        }

        if (!ok || returned <= headerSize) return null;
        return buffer.AsSpan(headerSize, (int)returned - headerSize).ToArray();
    }

    /// <summary>
    /// Walks the BOS device capability list looking for the Billboard capability.
    ///
    /// Billboard capability layout: iAdditionalInfoURL(1), bNumberOfAlternateOrUSB4Modes(1),
    /// bPreferredAlternateOrUSB4Mode(1), VCONNPower(2), bmConfigured(32), bcdVersion(2),
    /// bAdditionalFailureInfo(1), bReserved(1), then four bytes per mode: wSVID(2),
    /// bAlternateOrUSB4Mode(1), iAlternateOrUSB4ModeString(1).
    /// </summary>
    private static BillboardReport? ParseBillboard(byte[] bos, ushort vid, ushort pid)
    {
        int offset = 5;   // past the BOS header

        while (offset + 3 <= bos.Length)
        {
            byte length = bos[offset];
            if (length < 3 || offset + length > bos.Length) break;

            if (bos[offset + 1] == DescriptorTypeDeviceCapability &&
                bos[offset + 2] == CapabilityTypeBillboard)
            {
                return ParseCapability(bos.AsSpan(offset, length), vid, pid);
            }

            offset += length;
        }

        return null;
    }

    private static BillboardReport? ParseCapability(ReadOnlySpan<byte> cap, ushort vid, ushort pid)
    {
        const int modesOffset = 44;   // 3 header + 1 + 1 + 1 + 2 + 32 + 2 + 1 + 1
        if (cap.Length < modesOffset) return null;

        int modeCount = cap[4];
        int preferred = cap[5];
        ReadOnlySpan<byte> configured = cap.Slice(8, 32);

        var report = new BillboardReport
        {
            VendorId = $"0x{vid:X4}",
            ProductId = $"0x{pid:X4}",
            PreferredModeIndex = preferred,
        };

        for (int i = 0; i < modeCount; i++)
        {
            int entry = modesOffset + (i * 4);
            if (entry + 4 > cap.Length) break;

            ushort svid = (ushort)(cap[entry] | (cap[entry + 1] << 8));
            byte mode = cap[entry + 2];
            int state = (configured[i / 4] >> ((i % 4) * 2)) & 0x03;

            report.Modes.Add(new AlternateModeReport
            {
                Index = i,
                Svid = $"0x{svid:X4}",
                Name = SvidName(svid),
                ModeNumber = mode,
                State = StateName(state),
                Entered = state == 3,
                IsDisplayPort = svid == SvidDisplayPort,
            });
        }

        report.CarriesVideo = report.Modes.Any(m => m.IsDisplayPort && m.Entered);
        report.SupportsVideo = report.Modes.Any(m => m.IsDisplayPort);
        return report;
    }

    /// <summary>Standard IDs assigned by the USB-IF and VESA. Anything else is reported as its number.</summary>
    public static string SvidName(ushort svid) => svid switch
    {
        SvidDisplayPort => "DisplayPort Alternate Mode",
        0x8087 => "Intel Thunderbolt 3",
        0x17EF => "Lenovo vendor mode",
        0xFF00 => "USB Type-C Bridge",
        _ => $"vendor-specific SVID 0x{svid:X4}",
    };

    private static string StateName(int state) => state switch
    {
        0 => "unspecified error",
        1 => "not attempted",
        2 => "attempted but failed",
        3 => "entered successfully",
        _ => "unknown",
    };
}
