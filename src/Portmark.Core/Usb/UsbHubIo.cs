using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Portmark.Core.Native;

namespace Portmark.Core.Usb;

/// <summary>One device attached to a hub port, as the hub reports it.</summary>
public sealed record UsbConnection(
    uint Port, ushort VendorId, ushort ProductId, byte DeviceClass, byte DeviceSubClass,
    byte DeviceProtocol, byte Speed, byte ConfigurationValue, bool IsHub, ushort Address,
    ushort UsbVersion, byte ManufacturerStringIndex, byte ProductStringIndex,
    byte SerialStringIndex);

/// <summary>
/// Shared access to USB hubs through the documented hub IOCTLs. None of this needs elevation or
/// any registry change, which is what makes it usable on a first run.
///
/// The layout note that matters: USB_NODE_CONNECTION_INFORMATION_EX is byte-packed, because the
/// USB_DEVICE_DESCRIPTOR it embeds is declared with pshpack1. Every field after the descriptor is
/// therefore at an offset one lower than natural alignment would put it.
/// </summary>
public static class UsbHubIo
{
    private const uint FileDeviceUsb = 0x22;

    internal static readonly uint IoctlGetNodeInformation = Win32.CtlCode(FileDeviceUsb, 258, 0, 0);
    internal static readonly uint IoctlGetDescriptorFromNodeConnection = Win32.CtlCode(FileDeviceUsb, 260, 0, 0);
    internal static readonly uint IoctlGetNodeConnectionInformationEx = Win32.CtlCode(FileDeviceUsb, 274, 0, 0);

    public static readonly Guid HubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    public const byte DescriptorTypeConfiguration = 0x02;
    public const byte DescriptorTypeString = 0x03;
    public const byte DescriptorTypeBos = 0x0F;

    public static IReadOnlyList<string> EnumerateHubs() => DeviceInterfaces.FindAll(HubInterface);

    public static SafeFileHandle OpenHub(string path) => Win32.CreateFile(
        path, Win32.GENERIC_WRITE, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
        IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

    public static unsafe int GetPortCount(SafeFileHandle hub)
    {
        byte[] node = new byte[128];
        fixed (byte* p = node)
        {
            if (!Win32.DeviceIoControl(hub, IoctlGetNodeInformation, p, (uint)node.Length,
                                       p, (uint)node.Length, out _, IntPtr.Zero))
                return 0;
        }
        // USB_NODE_INFORMATION: NodeType (4), then USB_HUB_DESCRIPTOR with bNumberOfPorts at +2.
        return node[6];
    }

    /// <summary>Reads what is attached to a port, or null when the port is empty.</summary>
    public static unsafe UsbConnection? GetConnection(SafeFileHandle hub, uint port)
    {
        byte[] b = new byte[1024];
        BitConverter.TryWriteBytes(b, port);

        fixed (byte* p = b)
        {
            if (!Win32.DeviceIoControl(hub, IoctlGetNodeConnectionInformationEx,
                                       p, (uint)b.Length, p, (uint)b.Length, out _, IntPtr.Zero))
                return null;
        }

        //  0     ConnectionIndex (4)
        //  4-21  DeviceDescriptor (18)
        //  22    CurrentConfigurationValue   23 Speed   24 DeviceIsHub
        //  25-26 DeviceAddress   27-30 NumberOfOpenPipes   31-34 ConnectionStatus
        if (BitConverter.ToUInt32(b, 31) != 1) return null;   // 1 == DeviceConnected

        return new UsbConnection(
            Port: port,
            VendorId: BitConverter.ToUInt16(b, 12),
            ProductId: BitConverter.ToUInt16(b, 14),
            DeviceClass: b[8],
            DeviceSubClass: b[9],
            DeviceProtocol: b[10],
            Speed: b[23],
            ConfigurationValue: b[22],
            IsHub: b[24] != 0,
            Address: BitConverter.ToUInt16(b, 25),
            UsbVersion: BitConverter.ToUInt16(b, 6),
            ManufacturerStringIndex: b[18],
            ProductStringIndex: b[19],
            SerialStringIndex: b[20]);
    }

    /// <summary>Issues a GET_DESCRIPTOR control request through the hub on the device's behalf.</summary>
    public static unsafe byte[]? GetDescriptor(SafeFileHandle hub, uint port, byte type, byte index,
                                               ushort languageId, int length)
    {
        const int headerSize = 12;   // ConnectionIndex (4) + setup packet (8)
        byte[] buffer = new byte[headerSize + length];

        BitConverter.TryWriteBytes(buffer.AsSpan(0), port);
        buffer[4] = 0x80;   // device to host, standard, device
        buffer[5] = 0x06;   // GET_DESCRIPTOR
        BitConverter.TryWriteBytes(buffer.AsSpan(6), (ushort)((type << 8) | index));
        BitConverter.TryWriteBytes(buffer.AsSpan(8), languageId);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)length);

        bool ok;
        uint returned;
        fixed (byte* p = buffer)
        {
            ok = Win32.DeviceIoControl(hub, IoctlGetDescriptorFromNodeConnection,
                                       p, (uint)buffer.Length, p, (uint)buffer.Length,
                                       out returned, IntPtr.Zero);
        }

        if (!ok || returned <= headerSize) return null;
        return buffer.AsSpan(headerSize, (int)returned - headerSize).ToArray();
    }

    /// <summary>Reads a string descriptor, or null when the device has none at that index.</summary>
    public static string? GetString(SafeFileHandle hub, uint port, byte index, ushort languageId = 0x0409)
    {
        if (index == 0) return null;

        byte[]? data = GetDescriptor(hub, port, DescriptorTypeString, index, languageId, 255);
        if (data is null || data.Length < 4 || data[1] != DescriptorTypeString) return null;

        // bLength, bDescriptorType, then UTF-16LE characters.
        int chars = Math.Min(data[0], data.Length) - 2;
        if (chars <= 0) return null;

        string value = System.Text.Encoding.Unicode.GetString(data, 2, chars).Trim('\0').Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Maximum current the device asks for, from its configuration descriptor.</summary>
    public static int? GetMaxPowerMilliamps(SafeFileHandle hub, uint port, byte speed)
    {
        byte[]? config = GetDescriptor(hub, port, DescriptorTypeConfiguration, 0, 0, 9);
        if (config is null || config.Length < 9 || config[1] != DescriptorTypeConfiguration) return null;

        // bMaxPower is at offset 8, in 2 mA units, except SuperSpeed where the unit is 8 mA.
        int unit = speed >= 3 ? 8 : 2;
        return config[8] * unit;
    }

    public static string SpeedName(byte speed) => speed switch
    {
        0 => "Low, 1.5 Mbps",
        1 => "Full, 12 Mbps",
        2 => "High, 480 Mbps",
        3 => "SuperSpeed, 5 Gbps or above",
        _ => $"unknown ({speed})",
    };

    /// <summary>USB device class codes, for the ones a user is likely to meet.</summary>
    public static string DeviceClassName(byte code) => code switch
    {
        0x00 => "declared per interface",
        0x01 => "Audio",
        0x02 => "Communications",
        0x03 => "Human Interface Device",
        0x05 => "Physical",
        0x06 => "Image",
        0x07 => "Printer",
        0x08 => "Mass Storage",
        0x09 => "Hub",
        0x0A => "CDC Data",
        0x0B => "Smart Card",
        0x0E => "Video",
        0x10 => "Audio/Video",
        0x11 => "Billboard",
        0xDC => "Diagnostic",
        0xE0 => "Wireless Controller",
        0xEF => "Miscellaneous",
        0xFE => "Application Specific",
        0xFF => "Vendor Specific",
        _ => $"class 0x{code:X2}",
    };
}
