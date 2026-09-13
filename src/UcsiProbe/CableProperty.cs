namespace UcsiProbe;

/// <summary>
/// Decodes a UCSI GET_CABLE_PROPERTY response.
///
/// Layout per the UCSI specification, 5 bytes:
///   bytes 0-1  bmSpeedSupported   bits 0-13 mantissa, bits 14-15 exponent
///   byte  2    bCurrentCapability in 50 mA units
///   byte  3    bit 0 bmVBUSInCable, bit 1 bIsActiveCable, bit 2 bDirectionality,
///              bits 3-4 bPlugEndType, bit 5 bmModeSupport, bits 6-7 cable type / PD revision
///   byte  4    bits 0-3 bLatency
///
/// Every field that the response does not actually determine is reported as unknown. A USB-C
/// socket tells you the shape of the connector and nothing else, so nothing here is inferred from
/// the presence of a port or from the shape of a plug.
/// </summary>
public static class CableProperty
{
    public static string Describe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5) return "response too short to decode";

        // An all-zero response is the PPM saying it has nothing, not a cable claiming to be a
        // passive Type-A cable rated 0 mA. Decoding it as fields would invent a cable.
        if (IsEmpty(data)) return "no cable data reported by the PPM";

        ushort speedRaw = (ushort)(data[0] | (data[1] << 8));
        byte currentRaw = data[2];
        byte flags = data[3];
        byte latency = (byte)(data[4] & 0x0F);

        bool vbusInCable = (flags & 0x01) != 0;
        bool activeCable = (flags & 0x02) != 0;
        bool directional = (flags & 0x04) != 0;
        int plugType = (flags >> 3) & 0x03;
        bool modeSupport = (flags & 0x20) != 0;

        var parts = new List<string>
        {
            $"speed {FormatSpeed(speedRaw)}",
            $"current {FormatCurrent(currentRaw)}",
            $"plug {PlugName(plugType)}",
            activeCable ? "active cable" : "passive cable",
        };

        if (vbusInCable) parts.Add("VBUS in cable");
        if (directional) parts.Add("directional");
        if (modeSupport) parts.Add("supports alternate modes");
        if (latency != 0) parts.Add($"latency code {latency}");

        return string.Join(", ", parts);
    }

    /// <summary>An all-zero payload carries no information at all.</summary>
    public static bool IsEmpty(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data[..Math.Min(5, data.Length)])
            if (b != 0) return false;
        return true;
    }

    /// <summary>
    /// bmSpeedSupported is a mantissa with a unit exponent. A zero mantissa means the cable did
    /// not report a speed, which is not the same as the cable being slow.
    /// </summary>
    public static string FormatSpeed(ushort raw)
    {
        int mantissa = raw & 0x3FFF;
        int exponent = (raw >> 14) & 0x03;
        if (mantissa == 0) return "unknown (cable reported no speed)";

        string unit = exponent switch
        {
            0 => "bps",
            1 => "kbps",
            2 => "Mbps",
            3 => "Gbps",
            _ => "?",
        };
        return $"{mantissa} {unit}";
    }

    /// <summary>bCurrentCapability is in 50 mA units. Zero means not reported, not zero amps.</summary>
    public static string FormatCurrent(byte raw)
        => raw == 0 ? "unknown (cable reported no current rating)" : $"{raw * 50} mA";

    public static string PlugName(int plugType) => plugType switch
    {
        0 => "USB Type-A",
        1 => "USB Type-B",
        2 => "USB Type-C",
        3 => "other or captive",
        _ => "unknown",
    };

    /// <summary>
    /// The plain English one-liner. Deliberately conservative: it only states what the e-marker
    /// actually reported, and says so when a field was not reported.
    /// </summary>
    public static string Human(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || IsEmpty(data)) return "No e-marker data. This cable did not identify itself.";

        ushort speedRaw = (ushort)(data[0] | (data[1] << 8));
        byte currentRaw = data[2];

        string power = currentRaw == 0
            ? "power rating not reported"
            : $"up to {currentRaw * 50 / 1000.0:0.#}A";
        string speed = (speedRaw & 0x3FFF) == 0 ? "speed not reported" : FormatSpeed(speedRaw);

        return $"{power}, {speed}. Video capability is not reported by this response.";
    }
}
