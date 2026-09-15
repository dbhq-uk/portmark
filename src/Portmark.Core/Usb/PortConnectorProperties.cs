namespace Portmark.Core.Usb;

/// <summary>
/// Which port, if any, shares a port's physical connector, from
/// IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES.
///
/// This exists because the first cut of the speed diagnostic was wrong about sockets. An xHCI
/// controller has separate USB 2 and USB 3 execution units, and Windows lists their ports under
/// separate numbers, even on one root hub: on the test machine root hub port 1 reports USB 1.1 and
/// USB 2.0, port 2 reports USB 3, and each names the other as its companion. A SuperSpeed device
/// that came up at High Speed sits on the USB 2 number, and "this port does not support USB 3"
/// was then true of the number and false of the socket the user plugged into.
///
/// Only CompanionIndex 0 is asked for. Microsoft documents it as always 0 for SuperSpeed hubs and
/// xHCI controllers; a port with several companions (<see cref="HasMultipleCompanions"/>) has the
/// rest left unread rather than guessed at.
///
/// Sources: USB_PORT_CONNECTOR_PROPERTIES, USB_PORT_PROPERTIES and
/// IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES in usbioctl.h, on Microsoft Learn.
/// </summary>
public sealed record PortConnectorProperties(
    uint UsbPortProperties, ushort CompanionPortNumber, string? CompanionHubSymbolicLinkName)
{
    /// <summary>ConnectionIndex, ActualLength, UsbPortProperties, CompanionIndex, CompanionPortNumber.</summary>
    public const int FixedSize = 16;

    /// <summary>USB_PORT_PROPERTIES bit 2, PortHasMultipleCompanions.</summary>
    public bool HasMultipleCompanions => (UsbPortProperties & 0x4) != 0;

    /// <summary>Decodes the structure, bounded by its ActualLength. Null when shorter than the fixed part.</summary>
    public static PortConnectorProperties? Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < FixedSize) return null;

        int actual = (int)Math.Min(BitConverter.ToUInt32(b[4..8]), (uint)b.Length);
        int nameBytes = Math.Max(0, actual - FixedSize) & ~1;

        string? name = null;
        if (nameBytes > 0)
        {
            name = System.Text.Encoding.Unicode.GetString(b.Slice(FixedSize, nameBytes));
            int end = name.IndexOf('\0');
            if (end >= 0) name = name[..end];
            if (name.Length == 0) name = null;
        }

        return new PortConnectorProperties(
            BitConverter.ToUInt32(b[8..12]), BitConverter.ToUInt16(b[14..16]), name);
    }
}
