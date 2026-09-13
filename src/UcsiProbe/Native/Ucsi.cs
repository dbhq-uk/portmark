namespace UcsiProbe.Native;

/// <summary>
/// UCSI protocol constants and IOCTL codes.
///
/// Sources (clean room — no upstream WhatCable source was read, see CLEANROOM.md):
///   * USB Type-C Connector System Software Interface (UCSI) Specification, USB-IF / Intel.
///   * Microsoft Learn, "USB-C Connector System Software Interface (UCSI) Driver"
///     (command encodings cross-checked against the documented UcsiControl.exe examples).
/// Numeric interface values (device type, control codes) are interface facts restated
/// from the public headers; no MS-PL sample code was copied.
/// </summary>
internal static class Ucsi
{
    /// <summary>FILE_DEVICE_UCSI.</summary>
    internal const uint FILE_DEVICE_UCSI = 4627;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;

    internal static readonly uint IOCTL_UCSI_SEND_COMMAND =
        Win32.CtlCode(FILE_DEVICE_UCSI, 0x401, METHOD_BUFFERED, FILE_ANY_ACCESS);
    internal static readonly uint IOCTL_UCSI_GET_CCI =
        Win32.CtlCode(FILE_DEVICE_UCSI, 0x402, METHOD_BUFFERED, FILE_ANY_ACCESS);
    internal static readonly uint IOCTL_UCSI_GET_MESSAGE =
        Win32.CtlCode(FILE_DEVICE_UCSI, 0x403, METHOD_BUFFERED, FILE_ANY_ACCESS);

    /// <summary>Device interface published by the WDK UcmCxUcsi sample driver.</summary>
    internal static readonly Guid GUID_DEVINTERFACE_UCSI_TEST =
        new("6c846eea-9649-46b3-b8cb-2cbc72c7b7fb");

    /// <summary>Interfaces the in-box Ucmcx stack publishes on the UCM-UCSI ACPI device.</summary>
    internal static readonly Guid GUID_DEVINTERFACE_UCM_A = new("4cedf9cf-34a7-486b-9036-20812ee4467c");
    internal static readonly Guid GUID_DEVINTERFACE_UCM_B = new("ae05a169-b410-4672-a8b6-a01a2de0dc78");

    internal static readonly Guid GUID_DEVINTERFACE_USB_HUB = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    internal static readonly Guid GUID_DEVINTERFACE_USB_HOST_CONTROLLER =
        new("3abf6f2d-71c4-462a-8a92-1e6861e6af27");

    // UCSI command opcodes (UCSI 1.2 + 2.x additions).
    internal const byte CMD_PPM_RESET = 0x01;
    internal const byte CMD_GET_CAPABILITY = 0x06;
    internal const byte CMD_GET_CONNECTOR_CAPABILITY = 0x07;
    internal const byte CMD_GET_ALTERNATE_MODES = 0x0C;
    internal const byte CMD_GET_CAM_SUPPORTED = 0x0D;
    internal const byte CMD_GET_PDOS = 0x10;
    internal const byte CMD_GET_CABLE_PROPERTY = 0x11;
    internal const byte CMD_GET_CONNECTOR_STATUS = 0x12;
    internal const byte CMD_GET_ERROR_STATUS = 0x13;

    /// <summary>
    /// Builds the 64-bit UCSI CONTROL value: command in bits 0-7, data length in bits 8-15,
    /// command-specific data from bit 16. Connector number occupies bits 16-22 for the
    /// per-connector commands. Matches the documented UcsiControl.exe encodings, e.g.
    /// GET_CONNECTOR_STATUS on connector 1 is 0x00010012.
    /// </summary>
    internal static ulong Control(byte command, byte dataLength = 0, ulong commandSpecific = 0)
        => command | ((ulong)dataLength << 8) | (commandSpecific << 16);

    internal static ulong ForConnector(byte command, byte connector)
        => Control(command, 0, (ulong)(connector & 0x7F));

    internal static string CommandName(byte command) => command switch
    {
        CMD_PPM_RESET => "PPM_RESET",
        CMD_GET_CAPABILITY => "GET_CAPABILITY",
        CMD_GET_CONNECTOR_CAPABILITY => "GET_CONNECTOR_CAPABILITY",
        CMD_GET_ALTERNATE_MODES => "GET_ALTERNATE_MODES",
        CMD_GET_CAM_SUPPORTED => "GET_CAM_SUPPORTED",
        CMD_GET_PDOS => "GET_PDOS",
        CMD_GET_CABLE_PROPERTY => "GET_CABLE_PROPERTY",
        CMD_GET_CONNECTOR_STATUS => "GET_CONNECTOR_STATUS",
        CMD_GET_ERROR_STATUS => "GET_ERROR_STATUS",
        _ => $"0x{command:X2}",
    };
}
