namespace Portmark.Core.Usb;

/// <summary>
/// What the hub reports about a port's USB protocols and the attached device's SuperSpeed
/// capability, from IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2.
///
/// This is the evidence the speed diagnostic rests on. The first version compared the negotiated
/// speed against bcdUSB, but bcdUSB is the specification a device conforms to, not a speed it can
/// reach: a full-speed-only mouse is a correct USB 2.0 device, and was flagged as slow.
///
/// Microsoft's pages disagree on one point that matters. The flags page says
/// DeviceIsSuperSpeedCapableOrHigher means the attached device is capable; the IOCTL page says it
/// is set when "the port and the attached device" are capable. So a clear bit is never taken as
/// proof that a device cannot do SuperSpeed, and the device's own BOS descriptor is read as well.
///
/// Sources: USB_NODE_CONNECTION_INFORMATION_EX_V2, USB_PROTOCOLS,
/// USB_NODE_CONNECTION_INFORMATION_EX_V2_FLAGS and IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2
/// in usbioctl.h, on Microsoft Learn.
/// </summary>
public sealed record ConnectionSpeedInfo(uint SupportedUsbProtocols, uint Flags)
{
    /// <summary>Four ULONGs: ConnectionIndex, Length, SupportedUsbProtocols, Flags.</summary>
    public const int Size = 16;

    /// <summary>USB 1.1 signalling. Microsoft notes this is never set for a USB 3.0 port.</summary>
    public bool PortSupportsUsb110 => (SupportedUsbProtocols & 0x1) != 0;
    public bool PortSupportsUsb200 => (SupportedUsbProtocols & 0x2) != 0;
    public bool PortSupportsUsb300 => (SupportedUsbProtocols & 0x4) != 0;

    /// <summary>
    /// True when the hub reported any protocol at all. An all-zero field is the hub having nothing
    /// to say about the port, not a port that supports nothing.
    /// </summary>
    public bool ProtocolsReported => (SupportedUsbProtocols & 0x7) != 0;

    public bool OperatingAtSuperSpeedOrHigher => (Flags & 0x1) != 0;
    public bool SuperSpeedCapableOrHigher => (Flags & 0x2) != 0;
    public bool OperatingAtSuperSpeedPlusOrHigher => (Flags & 0x4) != 0;
    public bool SuperSpeedPlusCapableOrHigher => (Flags & 0x8) != 0;

    /// <summary>
    /// The port that shares this port's physical connector: 0 when the hub reports none, null when
    /// the connector properties could not be read. Filled in by the caller from
    /// <see cref="PortConnectorProperties"/>, not decoded from the V2 structure.
    /// </summary>
    public ushort? CompanionPortNumber { get; init; }

    /// <summary>USB_PROTOCOLS of that companion port. Null when there is none or it could not be read.</summary>
    public uint? CompanionSupportedUsbProtocols { get; init; }

    public bool? CompanionSupportsUsb300 => CompanionSupportedUsbProtocols is uint c ? (c & 0x4) != 0 : null;

    public static ConnectionSpeedInfo? Decode(ReadOnlySpan<byte> v2)
    {
        if (v2.Length < Size) return null;
        return new ConnectionSpeedInfo(BitConverter.ToUInt32(v2[8..12]), BitConverter.ToUInt32(v2[12..16]));
    }

    /// <summary>"USB 2.0 and USB 3", as the hub reported them, or null when it reported none.</summary>
    public string? ProtocolList()
    {
        var parts = new List<string>();
        if (PortSupportsUsb110) parts.Add("USB 1.1");
        if (PortSupportsUsb200) parts.Add("USB 2.0");
        if (PortSupportsUsb300) parts.Add("USB 3");

        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => string.Join(", ", parts[..^1]) + " and " + parts[^1],
        };
    }
}

/// <summary>
/// The speed capabilities a device declares in its own BOS descriptor.
///
/// Only two capabilities are read. The SuperSpeed USB Device Capability carries wSpeedsSupported,
/// a bitmap of the speeds the device supports (bit 0 low, 1 full, 2 high, 3 5 Gbps). The
/// SuperSpeedPlus USB Device Capability is recorded as present, with its bytes, and not decoded
/// further: its sublink speed attributes describe lanes the device offers, which is not a speed
/// any link is running at.
///
/// Source: USB 3.2 specification, section 9.6.2, Binary Device Object Store.
/// </summary>
public sealed class BosSpeedCapability
{
    private const byte DescriptorTypeBos = 0x0F;
    private const byte DescriptorTypeDeviceCapability = 0x10;
    private const byte CapabilitySuperSpeed = 0x03;
    private const byte CapabilitySuperSpeedPlus = 0x0A;

    /// <summary>wSpeedsSupported, or null when the BOS has no SuperSpeed capability.</summary>
    public ushort? SpeedsSupported { get; private init; }

    public string? SuperSpeedCapabilityHex { get; private init; }
    public string? SuperSpeedPlusCapabilityHex { get; private init; }

    public bool DeclaresHighSpeed => SpeedsSupported is ushort s && (s & 0x04) != 0;
    public bool DeclaresSuperSpeed => SpeedsSupported is ushort s && (s & 0x08) != 0;
    public bool DeclaresSuperSpeedPlus => SuperSpeedPlusCapabilityHex is not null;

    /// <summary>Walks the capability list. Null when the bytes are not a BOS descriptor.</summary>
    public static BosSpeedCapability? Parse(ReadOnlySpan<byte> bos)
    {
        if (bos.Length < 5 || bos[0] < 5 || bos[1] != DescriptorTypeBos) return null;

        int total = Math.Min(BitConverter.ToUInt16(bos[2..4]), bos.Length);
        ushort? speeds = null;
        string? superSpeed = null, superSpeedPlus = null;

        int offset = bos[0];
        while (offset + 3 <= total)
        {
            byte length = bos[offset];
            if (length < 3 || offset + length > total) break;

            ReadOnlySpan<byte> cap = bos.Slice(offset, length);
            if (cap[1] == DescriptorTypeDeviceCapability)
            {
                if (cap[2] == CapabilitySuperSpeed && length >= 10)
                {
                    superSpeed = Convert.ToHexString(cap);
                    speeds = BitConverter.ToUInt16(cap[4..6]);
                }
                else if (cap[2] == CapabilitySuperSpeedPlus && length >= 12)
                {
                    superSpeedPlus = Convert.ToHexString(cap);
                }
            }

            offset += length;
        }

        return new BosSpeedCapability
        {
            SpeedsSupported = speeds,
            SuperSpeedCapabilityHex = superSpeed,
            SuperSpeedPlusCapabilityHex = superSpeedPlus,
        };
    }
}
