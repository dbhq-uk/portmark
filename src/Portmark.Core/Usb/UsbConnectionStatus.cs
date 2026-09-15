using Portmark.Core.Model;

namespace Portmark.Core.Usb;

/// <summary>
/// The hub's USB_CONNECTION_STATUS for a port.
///
/// The scan used to discard every status other than DeviceConnected. A port where enumeration
/// failed, or where the hub saw an overcurrent, has no working device, so it never appeared
/// anywhere, and those are exactly the ports worth knowing about.
///
/// Every description is the hub's statement and is worded as one. Microsoft names one member
/// DeviceCausedOvercurrent, but the documented meaning is that a connection attempt failed with an
/// overcurrent condition. Nothing here says what caused that, because nothing read here knows.
///
/// Source: USB_CONNECTION_STATUS in usbioctl.h, on Microsoft Learn.
/// </summary>
public static class UsbConnectionStatus
{
    public const uint NoDeviceConnected = 0;
    public const uint DeviceConnected = 1;
    public const uint DeviceFailedEnumeration = 2;
    public const uint DeviceGeneralFailure = 3;
    public const uint DeviceCausedOvercurrent = 4;
    public const uint DeviceNotEnoughPower = 5;
    public const uint DeviceNotEnoughBandwidth = 6;
    public const uint DeviceHubNestedTooDeeply = 7;
    public const uint DeviceInLegacyHub = 8;
    public const uint DeviceEnumerating = 9;
    public const uint DeviceReset = 10;

    /// <summary>
    /// USB_NODE_CONNECTION_INFORMATION_EX is byte-packed, so ConnectionStatus sits at 31, directly
    /// after NumberOfOpenPipes. See <see cref="UsbHubIo"/> for the whole layout.
    /// </summary>
    public const int ConnectionStatusOffset = 31;
    public const int ConnectionInformationSize = 35;

    public static string Name(uint status) => status switch
    {
        NoDeviceConnected => "NoDeviceConnected",
        DeviceConnected => "DeviceConnected",
        DeviceFailedEnumeration => "DeviceFailedEnumeration",
        DeviceGeneralFailure => "DeviceGeneralFailure",
        DeviceCausedOvercurrent => "DeviceCausedOvercurrent",
        DeviceNotEnoughPower => "DeviceNotEnoughPower",
        DeviceNotEnoughBandwidth => "DeviceNotEnoughBandwidth",
        DeviceHubNestedTooDeeply => "DeviceHubNestedTooDeeply",
        DeviceInLegacyHub => "DeviceInLegacyHub",
        DeviceEnumerating => "DeviceEnumerating",
        DeviceReset => "DeviceReset",
        _ => $"undocumented ({status})",
    };

    /// <summary>
    /// The statuses Microsoft documents as a failed connection attempt. Enumerating and reset are
    /// work in progress, and an undocumented value is reported but never promoted to a fault.
    /// </summary>
    public static bool IsFault(uint status) => status is >= DeviceFailedEnumeration and <= DeviceInLegacyHub;

    public static string Describe(uint status) => status switch
    {
        NoDeviceConnected => "the hub reports no device connected",
        DeviceConnected => "the hub reports a device connected",
        DeviceFailedEnumeration => "the hub reports that a device was attached and its enumeration failed",
        DeviceGeneralFailure => "the hub reports that a connection attempt failed, with no more specific status",
        DeviceCausedOvercurrent => "the hub reports that a connection attempt failed with an overcurrent condition",
        DeviceNotEnoughPower => "the hub reports that a connection attempt failed with not enough power to drive the device",
        DeviceNotEnoughBandwidth => "the hub reports that a connection attempt failed with not enough bandwidth for the device",
        DeviceHubNestedTooDeeply => "the hub reports that a connection attempt failed with hubs nested too deeply",
        DeviceInLegacyHub => "the hub reports that a connection attempt failed on an unsupported legacy hub",
        DeviceEnumerating => "the hub reports that the attached device is being enumerated",
        DeviceReset => "the hub reports that the attached device is being reset",
        _ => $"the hub reports connection status {status}, which Microsoft does not document",
    };

    /// <summary>
    /// Builds the report for one port from its USB_NODE_CONNECTION_INFORMATION_EX bytes.
    ///
    /// A faulted port may carry a partly read device descriptor or none at all. The vendor and
    /// product are kept only when the embedded descriptor identifies itself as one (bLength 18,
    /// bDescriptorType 1); an all-zero descriptor is the hub having nothing to say, not vendor 0.
    /// </summary>
    public static UsbPortStatusReport Decode(ReadOnlySpan<byte> info, string hubPath, uint port)
    {
        if (info.Length < ConnectionInformationSize)
            throw new ArgumentException("connection information is shorter than its fixed part", nameof(info));

        uint status = BitConverter.ToUInt32(info[ConnectionStatusOffset..(ConnectionStatusOffset + 4)]);
        bool described = info[4] == 0x12 && info[5] == 0x01;

        return new UsbPortStatusReport
        {
            HubPath = hubPath,
            Port = (int)port,
            ConnectionStatusCode = (int)status,
            ConnectionStatus = Name(status),
            IsFault = IsFault(status),
            Description = Describe(status),
            VendorId = described ? $"0x{BitConverter.ToUInt16(info[12..14]):X4}" : null,
            ProductId = described ? $"0x{BitConverter.ToUInt16(info[14..16]):X4}" : null,
            ConnectionInformationHex = Convert.ToHexString(info[..ConnectionInformationSize]),
        };
    }
}
