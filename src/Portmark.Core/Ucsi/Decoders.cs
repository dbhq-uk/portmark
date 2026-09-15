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
                VideoNote = "Not determinable from the cable. UCSI does not report a cable's video capability; what the port and the attached device offer is under alternate modes.",
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
            VideoNote = "Not determinable from the cable. UCSI does not report a cable's video "
                      + "capability; its alternate mode support flag is the closest available signal.",
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

    /// <summary>
    /// Formats a BCD version as major.minor.subminor, dropping a zero subminor.
    ///
    /// The subminor nibble must not be discarded. This machine reports bcdBcVersion as 0x0102,
    /// which is 1.0.2; truncating to major.minor silently rendered it as "1.0". The firmware very
    /// likely means Battery Charging 1.2 and has encoded it non-standardly, but guessing that on
    /// the device's behalf would be inventing a version it did not report.
    /// </summary>
    private static string Bcd(byte low, byte high)
    {
        ushort value = (ushort)(low | (high << 8));
        int major = (value >> 8) & 0xFF;
        int minor = (value >> 4) & 0x0F;
        int subminor = value & 0x0F;

        return subminor == 0 ? $"{major:X}.{minor:X}" : $"{major:X}.{minor:X}.{subminor:X}";
    }
}

/// <summary>
/// Decodes a UCSI GET_CONNECTOR_CAPABILITY response.
///
///   bits 0-7  bmOperationMode
///             bit 0 Rp only, bit 1 Rd only, bit 2 DRP, bit 3 analog audio accessory,
///             bit 4 debug accessory, bit 5 USB2, bit 6 USB3, bit 7 alternate mode
///   bit    8  provider, bit 9 consumer
///
/// This is what the connector itself can do, as distinct from what is plugged into it.
/// </summary>
public static class ConnectorCapability
{
    public static ConnectorCapabilityReport? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return null;

        ushort raw = (ushort)(data[0] | (data[1] << 8));
        byte modes = (byte)(raw & 0xFF);

        return new ConnectorCapabilityReport
        {
            SupportsUsb2 = (modes & (1 << 5)) != 0,
            SupportsUsb3 = (modes & (1 << 6)) != 0,
            SupportsAlternateModes = (modes & (1 << 7)) != 0,
            SupportsDualRolePower = (modes & (1 << 2)) != 0,
            SupportsAudioAccessory = (modes & (1 << 3)) != 0,
            SupportsDebugAccessory = (modes & (1 << 4)) != 0,
            CanProvidePower = (raw & (1 << 8)) != 0,
            CanConsumePower = (raw & (1 << 9)) != 0,
            Raw = $"0x{raw:X4}",
        };
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

        // Partner flags: bit 0 USB, bit 1 Alternate Mode, bits 2-3 USB4 Gen 3 and Gen 4 (UCSI 2.0).
        // With the 65W charger attached this reads 0x01: USB, no alternate mode, which is the
        // status-side evidence that the current-mode byte of 0 on that port means nothing.
        report.PartnerFlags = (int)Bits(data, 21, 8);
        report.PartnerAlternateModeFlag = Bit(data, 22);

        // Bits 64-65, the ninth byte, present from UCSI 1.0. This was read off the wire and
        // discarded until it turned out to be the field Windows drives its own slow-charging
        // notification from.
        if (data.Length >= 9)
        {
            int charging = (int)Bits(data, 64, 2);
            report.BatteryChargingStatusCode = charging;
            report.BatteryChargingStatus = ChargeDiagnostic.StatusLabel(charging);
        }
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

/// <summary>
/// Decodes GET_ALTERNATE_MODES: six bytes per mode, a 16-bit SVID then a 32-bit mode ID, up to
/// two modes per response. Captured from the ThinkPad T16 Gen 2 (AMD), whose connector 1 lists
/// 0x17EF (Lenovo), 0x8087 (Intel Thunderbolt 3) and 0xFF01 (DisplayPort), and whose connector 2
/// lists the first and last of those: the USB4 port and the plain USB-C port, respectively.
///
/// A response shorter than six bytes carries no modes. That is the normal answer for a partner
/// that offers none, such as a charger, and must not be read as a failure.
/// </summary>
public static class AlternateModes
{
    public const int BytesPerMode = 6;

    /// <summary>
    /// Upper bound on modes walked per recipient. The offset field is a byte, but a controller
    /// that never ends its list must not be walked for 256 round trips.
    /// </summary>
    public const int MaxModes = 16;

    /// <summary>
    /// Decodes one response. A record whose SVID is zero is not a mode: 0x0000 is not an assigned
    /// SVID, and an all-zero response is the hardware saying nothing. Rejected records still
    /// consume their offset, so the offsets of the modes that follow stay true to the controller.
    /// </summary>
    public static List<PortAlternateModeReport> Decode(ReadOnlySpan<byte> data, int firstOffset = 0)
    {
        var modes = new List<PortAlternateModeReport>();
        for (int i = 0, record = 0; i + BytesPerMode <= data.Length; i += BytesPerMode, record++)
        {
            ushort svid = (ushort)(data[i] | (data[i + 1] << 8));
            if (svid == 0) continue;

            uint mid = BitConverter.ToUInt32(data.Slice(i + 2, 4));
            modes.Add(new PortAlternateModeReport
            {
                Offset = firstOffset + record,
                Svid = $"0x{svid:X4}",
                Name = Usb.BillboardReader.SvidName(svid),
                ModeId = $"0x{mid:X8}",
                IsDisplayPort = svid == Usb.BillboardReader.SvidDisplayPort,
            });
        }
        return modes;
    }

    /// <summary>
    /// Walks the list for one recipient. <paramref name="query"/> issues GET_ALTERNATE_MODES at an
    /// offset and returns the result. It is a parameter so the walk can be tested against
    /// controllers that answer one mode per page, repeat a page, or fail part way through.
    ///
    /// The walk advances by the number of records the controller returned, not by a fixed two,
    /// and ends on an empty page. A page identical to the previous one means the controller is
    /// ignoring the offset, and the list is reported incomplete rather than padded with
    /// duplicates. A failure after some modes keeps them and says the list is incomplete; a
    /// failure before any says there is no data. Both differ from a controller that simply lists
    /// nothing, which is a complete, empty answer. The first version collapsed all four into an
    /// empty list, which then read as "none".
    /// </summary>
    public static AlternateModeListReport Enumerate(Func<byte, UcsiResult> query)
    {
        var report = new AlternateModeListReport();
        byte[]? previousPayload = null;
        byte offset = 0;

        while (true)
        {
            UcsiResult r = query(offset);

            string? failure = !r.Ok ? r.Error ?? "the request failed"
                : r.NotSupported ? "the controller reports GET_ALTERNATE_MODES as not supported"
                : r.Errored ? "the controller returned an error"
                : null;
            if (failure is not null)
            {
                report.DataAvailable = report.Modes.Count > 0;
                report.Reason = report.Modes.Count > 0
                    ? $"The list stopped after {report.Modes.Count} mode(s): {failure}."
                    : $"{char.ToUpperInvariant(failure[0])}{failure[1..]}.";
                return report;
            }

            report.DataAvailable = true;

            int records = r.Payload.Length / BytesPerMode;
            if (records == 0)
            {
                report.Complete = true;
                return report;
            }

            if (previousPayload is not null && r.Payload.AsSpan().SequenceEqual(previousPayload))
            {
                report.Reason = $"The controller returned the same page at offset {offset} as at "
                              + "the previous offset, so the list cannot be walked and may be incomplete.";
                return report;
            }
            previousPayload = r.Payload;

            report.Modes.AddRange(Decode(r.Payload, offset));
            offset = (byte)(offset + records);

            if (offset >= MaxModes)
            {
                report.Reason = $"Stopped after {MaxModes} modes without the controller ending the list.";
                return report;
            }
        }
    }

    /// <summary>
    /// Turns the two lists, the attachment state and the raw current-mode byte into the sentence
    /// portmark prints and the mode it names, if it names one. A mode is named only when the
    /// controller's index points at a port mode that the partner also offers. Everything else is
    /// stated as unknown, with the reason, never as absent.
    /// </summary>
    public static (string Note, PortAlternateModeReport? Active, bool Confirmed) Interpret(
        AlternateModeListReport supported, AlternateModeListReport? partner, bool? connected, byte? currentCam,
        bool? partnerAlternateModeFlag = null)
    {
        if (!supported.DataAvailable)
            return ($"This port's alternate modes could not be listed. {supported.Reason}", null, false);

        if (supported.Modes.Count == 0)
            return (supported.Complete
                ? "This controller lists no alternate modes for this port."
                : $"This controller listed no alternate modes for this port but did not end the list. {supported.Reason}",
                null, false);

        string canEnter = $"This port can enter: {Names(supported.Modes)}"
                        + (supported.Complete ? "." : $" (list incomplete: {supported.Reason})");

        if (connected is null)
            return ($"{canEnter} Whether anything is attached could not be read, so the partner was not asked.", null, false);
        if (connected == false)
            return ($"{canEnter} Nothing is attached.", null, false);

        // What the partner said, if anything. On this controller the partner list is empty and the
        // status flag is clear even with a DisplayPort adapter whose Billboard says DisplayPort
        // was entered, so neither can veto the controller's own index. They corroborate it when
        // they are present, and that is all.
        bool partnerListed = partner is { DataAvailable: true } && partner.Modes.Count > 0;
        string partnerText = partner is null || !partner.DataAvailable
            ? $"The attached device's modes could not be read{(partner?.Reason is { } why ? $": {why}" : ".")}"
            : partner.Modes.Count == 0
                ? partner.Complete
                    ? "The attached device listed no alternate modes."
                    : $"The attached device listed no alternate modes but did not end the list. {partner.Reason}"
                : $"The attached device offers: {Names(partner.Modes)}"
                  + (partner.Complete ? "." : $" (list incomplete: {partner.Reason})");

        // The controller's own answer. 0xFF is the specification's "no mode".
        if (currentCam is null)
            return ($"{canEnter} {partnerText} The controller did not report a current mode, so whether one is in use is unknown.", null, false);
        if (currentCam == 0xFF)
            return ($"{canEnter} {partnerText} The controller reports no alternate mode in use.", null, false);

        PortAlternateModeReport? candidate = supported.Modes.FirstOrDefault(m => m.Offset == currentCam);
        if (candidate is null)
            return ($"{canEnter} {partnerText} The controller's current-mode index {currentCam} does not match "
                  + "a listed port mode, so which mode is in use is unknown.", null, false);

        bool deviceListsIt = partnerListed && partner!.Modes.Any(p => p.Svid == candidate.Svid);
        if (deviceListsIt || partnerAlternateModeFlag == true)
            return ($"{canEnter} {partnerText} The controller reports {candidate.Name} as the current mode"
                  + (deviceListsIt ? ", and the device offers it." : ", and the connector status confirms an alternate mode is in operation."),
                    candidate, true);

        if (partnerListed)
            return ($"{canEnter} {partnerText} The controller's current-mode index points at {candidate.Name}, "
                  + "which the device did not list, so which mode is in use could not be confirmed.", null, false);

        // No word from the partner, which is this controller's normal state: it never lists a
        // partner's modes and never sets the alternate mode flag. Index 0 is ambiguous, being also
        // what it reports for an empty port, against the specification's 0xFF.
        //
        // A non-zero index is the controller's own statement and is passed on as exactly that,
        // unconfirmed. It is worth passing on and worth marking: a DisplayPort adapter on
        // connector 2 gave index 1, DisplayPort in that port's list, and its Billboard
        // independently confirmed the mode was entered. An iPhone on the same port gave the same
        // index 1 with no display in sight, and nothing available here can tell the two apart.
        // Naming the mode without the caveat would have been right once and wrong once.
        if (currentCam == 0)
            return ($"{canEnter} {partnerText} The controller's current-mode index is 0, which it also reports "
                  + "for an empty port, so no mode is confirmed in use.", null, false);
        return ($"{canEnter} {partnerText} The controller reports {candidate.Name} as the current mode. "
              + "Nothing here corroborates it: this controller does not list a partner's modes, and its "
              + "status flag says no alternate mode is in operation.", candidate, false);
    }

    private static string Names(IEnumerable<PortAlternateModeReport> modes)
        => string.Join(", ", modes.Select(m => m.Name));
}

/// <summary>
/// What the power contract says about the cable, on hardware that cannot read the cable at all.
///
/// This is the only deduction portmark makes, and it exists because the deduction is sound and
/// the alternative is staying silent about something the user can act on. Two rules combine.
/// USB Type-C requires any cable carrying more than 3A to be electronically marked, 3A being what
/// an unmarked cable may carry. USB Power Delivery then requires a source to read that marking,
/// over the cable, before it offers more than 3A: the compliance tests fail a non-captive source
/// that advertises above 3A without first sending Discover Identity to the cable.
///
/// So a supply advertising 5A has already done the cable read that this PC's controller cannot
/// do. portmark reports the conclusion and the evidence, never as something the cable said.
///
/// Two things it deliberately does not conclude. A captive cable is the standing exception, and
/// captive is indistinguishable from marked from this end, so both are stated. And current says
/// nothing about data speed: a 100W cable can be USB 2.0.
/// </summary>
public static class CableInference
{
    /// <summary>What any USB-C cable may carry without declaring itself, in milliamps.</summary>
    public const int UnmarkedCableLimitMilliamps = 3000;

    public static CableInferenceReport? FromPower(PowerReport power)
    {
        if (!power.DataAvailable) return null;

        // Only objects that decoded. An unrecognised object might hold anything, and guessing a
        // current out of one would be inventing the evidence for the conclusion.
        PowerObjectReport? highest = power.PartnerSource.Concat(power.LocalSource)
            .Where(p => p.Kind != "unrecognised" && p.MaxCurrentMilliamps is not null)
            .MaxBy(p => p.MaxCurrentMilliamps);

        if (highest?.MaxCurrentMilliamps is not int current || current <= UnmarkedCableLimitMilliamps)
            return null;

        string who = power.PartnerSource.Contains(highest) ? "The attached supply" : "This PC";

        return new CableInferenceReport
        {
            MinimumCurrentRatingMilliamps = current,
            Evidence = $"{who} advertises {highest.Display}.",
            Basis = $"A USB-C cable may carry {Amps(UnmarkedCableLimitMilliamps)} without declaring anything about "
                  + "itself. Above that it has to be electronically marked, and a supply has to read that marking "
                  + "over the cable before offering more. The one exception is a captive cable, which its supply "
                  + "knows by construction.",
            Conclusion = $"So the cable in use carries at least {Amps(current)}, on the word of the supply rather "
                       + "than of the cable. Whether it is captive or electronically marked cannot be told apart "
                       + "from this PC. This says nothing about its data speed: a cable can carry full power at "
                       + "USB 2.0 speed.",
        };
    }

    private static string Amps(int milliamps) => $"{milliamps / 1000.0:0.#}A";
}
