using Portmark.Core.Native;

namespace Portmark.Core.Ucsi;

/// <summary>
/// UCSI protocol constants and the Windows interface used to reach the PPM.
///
/// Derived from the USB-IF UCSI specification and Microsoft Learn documentation, plus direct
/// observation of the in-box driver on the machine under test.
/// </summary>
public static class UcsiProtocol
{
    /// <summary>
    /// The device interface published by the in-box UcmUcsiCx.sys when TestInterfaceEnabled is
    /// set. This is NOT the WDK sample's GUID_DEVINTERFACE_UCSI_TEST, which does not exist on a
    /// stock machine. See SPIKE.md for how this was established.
    /// </summary>
    public static readonly Guid TestInterface = new("0d3bc324-0125-4e95-b25a-7b393333ea9a");

    public const uint FileDeviceUcsi = 4627;

    /// <summary>
    /// Reads and writes the whole 48-byte UCSI data structure in one call. METHOD_BUFFERED shares
    /// one system buffer for input and output, so this is a read-modify-write.
    /// </summary>
    public static readonly uint IoctlDataBlock = Win32.CtlCode(FileDeviceUcsi, 0x406, 0, 0);

    // Layout of the UCSI data structure, total 48 bytes.
    public const int BlockSize = 48;
    public const int OffsetVersion = 0;
    public const int OffsetCci = 4;
    public const int OffsetControl = 8;
    public const int OffsetMessageIn = 16;
    public const int MessageInSize = 16;

    // UCSI command opcodes. Only read-only commands are listed: this library never sends a SET_*,
    // a reset, or a firmware command, and there is no code path that could.
    public const byte CmdAckCcCi = 0x04;
    public const byte CmdGetCapability = 0x06;
    public const byte CmdGetConnectorCapability = 0x07;
    public const byte CmdGetAlternateModes = 0x0C;
    public const byte CmdGetCamSupported = 0x0D;
    public const byte CmdGetCurrentCam = 0x0E;
    public const byte CmdGetPdos = 0x10;
    public const byte CmdGetCableProperty = 0x11;
    public const byte CmdGetConnectorStatus = 0x12;
    public const byte CmdGetErrorStatus = 0x13;

    /// <summary>
    /// GET_PD_MESSAGE, UCSI 1.2 and 2.0 section 4.5.20, code 0x15 (Table A-1; Linux
    /// UCSI_GET_PD_MESSAGE). It changes no port state, which is why it belongs in this list, but
    /// with Message Offset 0 it makes the controller send a PD request to the partner or cable, so
    /// it is gated by <see cref="PdMessage.WhyNotAsk"/> rather than sent wherever it might work.
    /// </summary>
    public const byte CmdGetPdMessage = 0x15;

    // GET_PD_MESSAGE Recipient, bits 23-25: 0 connector, 1 SOP, 2 SOP', 3 SOP'', 4-7 reserved.
    public const byte PdMessageRecipientSop = 1;
    public const byte PdMessageRecipientSopPrime = 2;

    // GET_PD_MESSAGE Response Message Type, bits 42-47: 0 Sink_Capabilities_Extended, 1
    // Source_Capabilities_Extended, 2 Battery_Capabilities, 3 Battery_Status, 4 "Discover Identity
    // Response - ACK, NAK or BUSY (Structured VDM)", 5-63 reserved in UCSI 2.0.
    public const byte PdMessageDiscoverIdentity = 4;

    // CCI indicator bits, per UCSI Table 4-2 and Microsoft's UCSI_CCI: bits 1-7 connector change,
    // 8-15 data length, then from bit 25 upward Not Supported, Cancel Completed, Reset Completed,
    // Busy, Acknowledge Command, Error, Command Completed.
    //
    // These sat two bits low once. That misread this controller's "Not Supported" answer to
    // GET_CABLE_PROPERTY (0x82000000) as a bare completion, and its "Error" answer to a
    // mis-encoded GET_ALTERNATE_MODES (0xC0000000) as an empty success, and both misreadings went
    // into the spike write-up. The undefined opcode 0x7F returns 0x82000000 too, which is the
    // check: an unknown command must come back Not Supported.
    public const uint CciNotSupported = 1u << 25;
    public const uint CciCancelCompleted = 1u << 26;
    public const uint CciResetCompleted = 1u << 27;
    public const uint CciBusy = 1u << 28;
    public const uint CciAcknowledge = 1u << 29;
    public const uint CciError = 1u << 30;
    public const uint CciCommandCompleted = 1u << 31;

    /// <summary>
    /// Builds the 64-bit CONTROL value: command in bits 0-7, data length in bits 8-15, and
    /// command-specific data from bit 16. Cross-checked against the encodings Microsoft documents
    /// for UcsiControl.exe, where GET_CONNECTOR_STATUS on connector 1 is 0x00010012.
    /// </summary>
    public static ulong Control(byte command, byte dataLength = 0, ulong commandSpecific = 0)
        => command | ((ulong)dataLength << 8) | (commandSpecific << 16);

    /// <summary>CONTROL for a command that targets a single connector.</summary>
    public static ulong ForConnector(byte command, byte connector)
        => Control(command, 0, (ulong)(connector & 0x7F));

    /// <summary>
    /// ACK_CC_CI with the Command Completed Acknowledge bit set. UCSI requires this after a
    /// command completes; without it the PPM stays waiting and rejects the next command.
    /// </summary>
    public static ulong AcknowledgeCommandCompleted() => Control(CmdAckCcCi, 0, 0x02);

    public static string CommandName(byte command) => command switch
    {
        CmdAckCcCi => "ACK_CC_CI",
        CmdGetCapability => "GET_CAPABILITY",
        CmdGetConnectorCapability => "GET_CONNECTOR_CAPABILITY",
        CmdGetAlternateModes => "GET_ALTERNATE_MODES",
        CmdGetCamSupported => "GET_CAM_SUPPORTED",
        CmdGetCurrentCam => "GET_CURRENT_CAM",
        CmdGetPdos => "GET_PDOS",
        CmdGetCableProperty => "GET_CABLE_PROPERTY",
        CmdGetConnectorStatus => "GET_CONNECTOR_STATUS",
        CmdGetErrorStatus => "GET_ERROR_STATUS",
        CmdGetPdMessage => "GET_PD_MESSAGE",
        _ => $"0x{command:X2}",
    };

    /// <summary>
    /// CONTROL for GET_ALTERNATE_MODES. This is the one command whose connector number is not at
    /// bit 16. The layout is Recipient 16-18, reserved 19-23, ConnectorNumber 24-30,
    /// AlternateModeOffset 32-39, NumberOfAlternateModes 40-41 (UCSI Table 4-17; Linux's
    /// UCSI_GET_ALTMODE_CONNECTOR_NUMBER shifts by 24).
    ///
    /// The first version packed the fields contiguously from bit 19. This controller then saw
    /// connector number zero, answered Error with "non-existent connector number", and the spike
    /// concluded it declined the command. It does not: with the number at bit 24 it lists every
    /// mode, and the count matches bNumAltModes from GET_CAPABILITY.
    /// Recipient: 0 connector, 1 SOP (the attached partner), 2 SOP', 3 SOP''.
    /// </summary>
    public static ulong GetAlternateModes(byte recipient, byte connector, byte offset,
                                          byte numberMinusOne = 0)
        => CmdGetAlternateModes
         | ((ulong)(recipient & 0x07) << 16)
         | ((ulong)(connector & 0x7F) << 24)
         | ((ulong)offset << 32)
         | ((ulong)(numberMinusOne & 0x03) << 40);

    /// <summary>
    /// CONTROL for GET_PDOS. Absolute bit offsets: ConnectorNumber 16-22, PartnerPdo 23,
    /// PdoOffset 24-31, NumberOfPdos 32-33, SourceOrSinkPdos 34, SourceCapabilitiesType 35-36.
    /// </summary>
    public static ulong GetPdos(byte connector, bool partner, byte offset,
                                byte numberMinusOne, bool source, byte sourceCapabilitiesType = 0)
        => CmdGetPdos
         | ((ulong)(connector & 0x7F) << 16)
         | ((partner ? 1UL : 0UL) << 23)
         | ((ulong)offset << 24)
         | ((ulong)(numberMinusOne & 0x03) << 32)
         | ((source ? 1UL : 0UL) << 34)
         | ((ulong)(sourceCapabilitiesType & 0x03) << 35);

    /// <summary>
    /// CONTROL for GET_PD_MESSAGE, UCSI 1.2 Table 4-50 and UCSI 2.0 Table 4-51. Absolute bit offsets:
    /// ConnectorNumber 16-22, Recipient 23-25, MessageOffset 26-33, NumberOfBytes 34-41,
    /// ResponseMessageType 42-47, reserved 48-63. Linux shifts by the same 23, 26, 34 and 42.
    ///
    /// Data Length stays zero. Number of Bytes must not exceed MAX_DATA_LENGTH, 16 here, and for a
    /// Structured VDM both it and the offset go in fours.
    /// </summary>
    public static ulong GetPdMessage(byte connector, byte recipient, byte offset, byte numberOfBytes,
                                     byte responseMessageType)
        => CmdGetPdMessage
         | ((ulong)(connector & 0x7F) << 16)
         | ((ulong)(recipient & 0x07) << 23)
         | ((ulong)offset << 26)
         | ((ulong)numberOfBytes << 34)
         | ((ulong)(responseMessageType & 0x3F) << 42);

    /// <summary>Formats the BCD version the PPM reports, e.g. 0x0100 becomes "1.0".</summary>
    public static string FormatVersion(ushort bcd)
        => $"{(bcd >> 8) & 0xFF:X}.{(bcd >> 4) & 0x0F:X}";
}
