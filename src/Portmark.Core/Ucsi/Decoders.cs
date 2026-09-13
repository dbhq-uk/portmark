using Portmark.Core.Model;

namespace Portmark.Core.Ucsi;

/// <summary>
/// Decodes a UCSI GET_CABLE_PROPERTY response.
///
/// Layout per the UCSI specification, 5 bytes:
///   bytes 0-1  bmSpeedSupported    bits 0-13 mantissa, bits 14-15 unit exponent
///   byte  2    bCurrentCapability  in 50 mA units
///   byte  3    bit 0 bmVBUSInCable, bit 1 bIsActiveCable, bit 2 bDirectionality,
///              bits 3-4 bPlugEndType, bit 5 bmModeSupport
///   byte  4    bits 0-3 bLatency
/// </summary>
public static class CableProperty
{
    /// <summary>
    /// An all-zero payload is the PPM saying it has nothing to report. Decoding it into fields
    /// would invent a passive Type-A cable rated 0 mA that does not exist.
    /// </summary>
    public static bool IsEmpty(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5) return true;
        foreach (byte b in data[..5])
            if (b != 0) return false;
        return true;
    }

    public static CableReport Decode(ReadOnlySpan<byte> data, string? emptyReason = null)
    {
        if (data.Length < 5 || IsEmpty(data))
        {
            return new CableReport
            {
                DataAvailable = false,
                Reason = emptyReason
                    ?? "The port controller reported no cable data. The cable carries no e-marker, "
                     + "or nothing is attached.",
                VideoNote = "Not determinable. UCSI does not report video capability.",
            };
        }

        ushort speedRaw = (ushort)(data[0] | (data[1] << 8));
        byte currentRaw = data[2];
        byte flags = data[3];
        int plugType = (flags >> 3) & 0x03;

        return new CableReport
        {
            DataAvailable = true,
            Speed = DecodeSpeed(speedRaw),
            CurrentCapabilityMilliamps = currentRaw == 0 ? null : currentRaw * 50,
            MaxWattsAt20Volts = currentRaw == 0 ? null : currentRaw * 50 * 20 / 1000,
            PlugType = PlugTypeName(plugType),
            ActiveCable = (flags & 0x02) != 0,
            VbusInCable = (flags & 0x01) != 0,
            SupportsAlternateModes = (flags & 0x20) != 0,
            LatencyCode = data[4] & 0x0F,
            SupportsVideo = null,
            VideoNote = "Not determinable. UCSI does not report video capability; "
                      + "alternate mode support is the closest available signal.",
        };
    }

    /// <summary>
    /// bmSpeedSupported is a mantissa plus a unit exponent. A zero mantissa means the cable did
    /// not state a speed, which is not the same as the cable being slow.
    /// </summary>
    public static SpeedReport? DecodeSpeed(ushort raw)
    {
        int mantissa = raw & 0x3FFF;
        if (mantissa == 0) return null;

        int exponent = (raw >> 14) & 0x03;
        (string unit, long multiplier) = exponent switch
        {
            0 => ("bps", 1L),
            1 => ("kbps", 1_000L),
            2 => ("Mbps", 1_000_000L),
            3 => ("Gbps", 1_000_000_000L),
            _ => ("?", 0L),
        };

        return new SpeedReport
        {
            Mantissa = mantissa,
            Unit = unit,
            Display = $"{mantissa} {unit}",
            BitsPerSecond = multiplier == 0 ? null : mantissa * multiplier,
        };
    }

    public static string PlugTypeName(int plugType) => plugType switch
    {
        0 => "USB Type-A",
        1 => "USB Type-B",
        2 => "USB Type-C",
        3 => "Other or captive",
        _ => "Unknown",
    };
}

/// <summary>
/// Decodes a UCSI GET_CAPABILITY response.
///
/// Layout per UCSI Table 4-13, cross-checked against the UCSI_GET_CAPABILITY_IN structure
/// Microsoft documents:
///   bytes 0-3  bmAttributes
///   byte  4    bNumConnectors, low 7 bits
///   bytes 5-7  bmOptionalFeatures, 24 bits
///   byte  8    bNumAltModes
///   bytes 10-11 bcdBcVersion, 12-13 bcdPdVersion, 14-15 bcdUsbTypeCVersion
///
/// bmOptionalFeatures bit 5 is CableDetailsAvailable. When it is clear, the controller will never
/// return cable data, and no amount of retrying or replugging will change that.
/// </summary>
public static class Capability
{
    public const int BitSetUom = 0;
    public const int BitSetPdm = 1;
    public const int BitAlternateModeDetails = 2;
    public const int BitAlternateModeOverride = 3;
    public const int BitPdoDetails = 4;
    public const int BitCableDetails = 5;
    public const int BitExternalSupplyNotification = 6;
    public const int BitPdResetNotification = 7;

    public static PpmFeatureReport? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9) return null;

        uint attributes = BitConverter.ToUInt32(data[..4]);
        uint optional = (uint)(data[5] | (data[6] << 8) | (data[7] << 16));

        return new PpmFeatureReport
        {
            SupportsBatteryCharging = (attributes & (1u << 1)) != 0,
            SupportsUsbPowerDelivery = (attributes & (1u << 2)) != 0,
            AlternateModeDetailsAvailable = (optional & (1u << BitAlternateModeDetails)) != 0,
            PowerDataObjectDetailsAvailable = (optional & (1u << BitPdoDetails)) != 0,
            CableDetailsAvailable = (optional & (1u << BitCableDetails)) != 0,
            AlternateModeCount = data[8],
            BatteryChargingVersion = data.Length >= 12 ? Bcd(data[10], data[11]) : null,
            PowerDeliveryVersion = data.Length >= 14 ? Bcd(data[12], data[13]) : null,
            TypeCVersion = data.Length >= 16 ? Bcd(data[14], data[15]) : null,
            OptionalFeaturesHex = $"0x{optional:X6}",
        };
    }

    public static int ConnectorCount(ReadOnlySpan<byte> data) => data.Length < 5 ? 0 : data[4] & 0x7F;

    private static string Bcd(byte low, byte high)
    {
        ushort value = (ushort)(low | (high << 8));
        return $"{(value >> 8) & 0xFF:X}.{(value >> 4) & 0x0F:X}";
    }
}

/// <summary>
/// Decodes a UCSI GET_CONNECTOR_STATUS response, a little-endian bit field:
///   bits  0-15  bmConnectorStatusChange
///   bits 16-18  bPowerOperationMode
///   bit     19  bConnectStatus
///   bit     20  bPowerDirection
///   bits 21-28  bmConnectorPartnerFlags
///   bits 29-31  bConnectorPartnerType
/// </summary>
public static class ConnectorStatus
{
    public static bool IsConnected(ReadOnlySpan<byte> data) => data.Length >= 3 && Bit(data, 19);

    public static void Apply(ReadOnlySpan<byte> data, ConnectorReport report)
    {
        if (data.Length < 5)
        {
            report.Connected = null;
            return;
        }

        report.Connected = Bit(data, 19);
        if (report.Connected != true) return;

        report.PowerOperationMode = PowerOperationModeName((int)Bits(data, 16, 3));
        report.PowerDirection = Bit(data, 20) ? "supplying" : "consuming";
        report.PartnerType = PartnerTypeName((int)Bits(data, 29, 3));
    }

    public static string PowerOperationModeName(int mode) => mode switch
    {
        1 => "USB default",
        2 => "BC 1.2 charger",
        3 => "USB Power Delivery",
        4 => "Type-C 1.5A",
        5 => "Type-C 3.0A",
        _ => $"Unknown ({mode})",
    };

    public static string PartnerTypeName(int type) => type switch
    {
        1 => "Downstream facing port",
        2 => "Upstream facing port",
        3 => "Cable, no upstream port",
        4 => "Cable with upstream port",
        5 => "Debug accessory",
        6 => "Audio accessory",
        _ => $"Unknown ({type})",
    };

    private static bool Bit(ReadOnlySpan<byte> data, int bit)
        => bit / 8 < data.Length && (data[bit / 8] & (1 << (bit % 8))) != 0;

    private static ulong Bits(ReadOnlySpan<byte> data, int start, int count)
    {
        ulong value = 0;
        for (int i = 0; i < count; i++)
            if (Bit(data, start + i))
                value |= 1UL << i;
        return value;
    }
}

/// <summary>
/// Decodes GET_ERROR_STATUS. This is what separates "the controller does not implement this
/// command" from "it does, and there is genuinely nothing attached to report".
/// </summary>
public static class ErrorStatus
{
    private static readonly (int Bit, string Meaning)[] Conditions =
    [
        (0,  "unrecognised command"),
        (1,  "non-existent connector number"),
        (2,  "invalid command-specific parameters"),
        (3,  "incompatible connector partner"),
        (4,  "CC communication error"),
        (5,  "command failed because of dead battery"),
        (6,  "contract negotiation failed"),
        (7,  "overcurrent"),
        (8,  "undefined"),
        (9,  "port partner rejected swap"),
        (10, "hard reset"),
        (11, "PPM policy conflict"),
        (12, "swap rejected"),
        (13, "reverse current protection"),
    ];

    public static string Describe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return "no error status returned";

        ushort flags = (ushort)(data[0] | (data[1] << 8));
        if (flags == 0) return "no error reported";

        string[] set = Conditions.Where(c => (flags & (1 << c.Bit)) != 0)
                                 .Select(c => c.Meaning)
                                 .ToArray();
        return set.Length > 0 ? string.Join("; ", set) : $"unrecognised error bits 0x{flags:X4}";
    }

    public static bool IsUnrecognisedCommand(ReadOnlySpan<byte> data)
        => data.Length >= 2 && (data[0] & 0x01) != 0;
}
