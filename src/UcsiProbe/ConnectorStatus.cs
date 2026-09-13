namespace UcsiProbe;

/// <summary>
/// Decodes a UCSI GET_CONNECTOR_STATUS response.
///
/// Layout per the UCSI specification, as a little-endian bit field starting at bit 0:
///   bits  0-15  bmConnectorStatusChange
///   bits 16-18  bPowerOperationMode
///   bit     19  bConnectStatus
///   bit     20  bPowerDirection
///   bits 21-28  bmConnectorPartnerFlags
///   bits 29-31  bConnectorPartnerType
///   bits 32-63  uRequestDataObject
///   bits 64-65  bBatteryChargingCapabilityStatus
///
/// Fields the response does not determine are reported as unknown rather than guessed.
/// </summary>
public static class ConnectorStatus
{
    public static string Describe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7) return "response too short to decode";

        bool connected = Bit(data, 19);
        if (!connected) return "nothing attached";

        int powerMode = (int)Bits(data, 16, 3);
        bool powerDirectionIsProvider = Bit(data, 20);
        int partnerType = (int)Bits(data, 29, 3);

        var parts = new List<string>
        {
            "attached",
            $"partner {PartnerTypeName(partnerType)}",
            $"power mode {PowerOperationModeName(powerMode)}",
            powerDirectionIsProvider ? "this port is supplying power" : "this port is consuming power",
        };

        if (data.Length >= 8)
        {
            uint rdo = (uint)Bits(data, 32, 32);
            if (rdo != 0) parts.Add($"request data object 0x{rdo:X8}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Whether anything is attached at all. This is the only honest source for that answer: the
    /// presence of a USB-C socket says nothing about what is in it.
    /// </summary>
    public static bool IsConnected(ReadOnlySpan<byte> data)
        => data.Length >= 3 && Bit(data, 19);

    public static string PowerOperationModeName(int mode) => mode switch
    {
        1 => "USB default",
        2 => "BC 1.2 charger",
        3 => "USB Power Delivery",
        4 => "Type-C 1.5A",
        5 => "Type-C 3.0A",
        _ => $"unknown ({mode})",
    };

    public static string PartnerTypeName(int type) => type switch
    {
        1 => "downstream facing port",
        2 => "upstream facing port",
        3 => "cable, no upstream port",
        4 => "cable with upstream port",
        5 => "debug accessory",
        6 => "audio accessory",
        _ => $"unknown ({type})",
    };

    private static bool Bit(ReadOnlySpan<byte> data, int bit)
        => (data[bit / 8] & (1 << (bit % 8))) != 0;

    private static ulong Bits(ReadOnlySpan<byte> data, int start, int count)
    {
        ulong value = 0;
        for (int i = 0; i < count; i++)
        {
            int bit = start + i;
            if (bit / 8 >= data.Length) break;
            if (Bit(data, bit)) value |= 1UL << i;
        }
        return value;
    }
}
