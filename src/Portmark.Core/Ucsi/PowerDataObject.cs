using Portmark.Core.Model;

namespace Portmark.Core.Ucsi;

/// <summary>
/// Decodes USB Power Delivery Power Data Objects, as returned by UCSI GET_PDOS, and the Request
/// Data Object from GET_CONNECTOR_STATUS.
///
/// Layouts per USB PD R3.2 section 6.4.1 (source power data objects), cross-checked against the
/// field definitions in Linux include/linux/usb/pd.h. A PDO is 32 bits. B31..30 select the shape
/// of the rest:
///   00 Fixed supply     B19..10 voltage (50 mV), B9..0 max current (10 mA)
///   01 Battery          B29..20 max voltage, B19..10 min voltage (50 mV), B9..0 max power (250 mW)
///   10 Variable supply  B29..20 max voltage, B19..10 min voltage (50 mV), B9..0 max current (10 mA)
///   11 Augmented        B29..28 pick the subtype:
///      00 SPR PPS       B27 PPS Power Limited, B24..17 max voltage, B15..8 min voltage (100 mV),
///                       B6..0 max current (50 mA)
///      01 EPR AVS       B27..26 peak current, B25..17 max voltage, B15..8 min voltage (100 mV),
///                       B7..0 PDP (1 W)
///      10 SPR AVS       B27..26 peak current, B19..10 max current for 9-15V, B9..0 max current
///                       for 15-20V (10 mA)
///      11 reserved
///
/// Verified against a 65W charger, which decoded to 5V/3A, 9V/3A, 15V/3A and 20V/3.25A: exactly
/// the profile printed on the supply.
/// </summary>
public static class PowerDataObject
{
    /// <summary>GET_PDOS Number of PDOs is two bits (UCSI 1.2 Table 4-34), so four per read.</summary>
    public const int ObjectsPerRead = 4;

    /// <summary>
    /// An SPR Source_Capabilities message carries at most seven objects (PD R3.2 section 6.4.1), and
    /// UCSI 1.2 section 4.5.15 answers Error to a GET_PDOS whose offset plus Number of PDOs field
    /// exceeds 7. So a list is at most two reads: offset 0 for four, offset 4 for three.
    /// </summary>
    public const int MaxSprObjects = 7;

    /// <summary>A source list as read: the objects that decoded, and the bytes they came from.</summary>
    public sealed record SourceList(List<PowerObjectReport> Objects, byte[] Bytes);

    /// <summary>
    /// Reads one side's source list. <paramref name="query"/> issues GET_PDOS at an offset with a
    /// Number of PDOs field, and is a parameter so the paging can be tested without a controller.
    ///
    /// The first version read offset 0 only, so objects five to seven of a supply that lists more
    /// than four were never seen. The walk stops on anything that is not a full, clean page: a
    /// failed call, the Error or Not Supported indicator, an empty or all-zero answer, or a page
    /// shorter than asked for, which is the list ending inside it. A second page that repeats the
    /// start of the first is a controller ignoring the offset, since no source lists the same
    /// object twice, and it is dropped rather than reported as three more offers.
    /// </summary>
    public static SourceList ReadSourceList(Func<byte, byte, UcsiResult> query)
    {
        var bytes = new List<byte>();
        byte[]? firstPage = null;
        int offset = 0;

        while (offset < MaxSprObjects)
        {
            int wanted = Math.Min(ObjectsPerRead, MaxSprObjects - offset);
            UcsiResult r = query((byte)offset, (byte)(wanted - 1));
            if (!r.Ok || r.Errored || r.NotSupported || r.NoPayload) break;

            int whole = Math.Min(r.Payload.Length / 4, wanted) * 4;
            byte[] page = r.Payload[..whole];

            if (firstPage is not null && firstPage.AsSpan().StartsWith(page)) break;
            firstPage ??= page;

            bytes.AddRange(page);
            if (whole < wanted * 4) break;
            offset += wanted;
        }

        byte[] all = [.. bytes];
        return new SourceList(DecodeAll(all, 1), all);
    }

    /// <summary>
    /// Decodes a run of objects. An all-zero word is an empty slot, not a supply offering nothing,
    /// and is not listed; but it still occupies its position. The first version dropped zero words
    /// and then counted positions through what was left, so an empty slot moved every later
    /// request onto the wrong object. Each object now carries the position it was read from.
    /// </summary>
    public static List<PowerObjectReport> DecodeAll(ReadOnlySpan<byte> data, int firstPosition = 1)
    {
        var results = new List<PowerObjectReport>();
        for (int offset = 0, position = firstPosition; offset + 4 <= data.Length; offset += 4, position++)
        {
            uint raw = BitConverter.ToUInt32(data.Slice(offset, 4));
            if (raw == 0) continue;
            results.Add(Decode(raw, position));
        }
        return results;
    }

    /// <summary>A structurally impossible object, reported as such rather than decoded.</summary>
    private static PowerObjectReport Unrecognised(uint raw, int? position) => new()
    {
        Kind = "unrecognised",
        Display = "unrecognised power object",
        Raw = $"0x{raw:X8}",
        Position = position,
    };

    /// <summary>
    /// A ranged object is only a supply if its range is a range and it offers something. Section
    /// 6.4.1 gives every one a minimum no higher than its maximum and a non-zero rating; bytes that
    /// break that are not the object their type bits claim, and decoding them anyway would hand
    /// the cable deduction a current nobody offered.
    /// </summary>
    private static bool Plausible(int minMv, int maxMv, int rating) => maxMv > 0 && minMv <= maxMv && rating > 0;

    public static PowerObjectReport Decode(uint raw, int? position = null)
    {
        int kind = (int)(raw >> 30) & 0x03;
        return kind switch
        {
            0 => Fixed(raw, position),
            1 => Battery(raw, position),
            2 => Variable(raw, position),
            _ => Augmented(raw, position),
        };
    }

    private static PowerObjectReport Fixed(uint raw, int? position)
    {
        int currentMa = (int)(raw & 0x3FF) * 10;
        int voltageMv = (int)((raw >> 10) & 0x3FF) * 50;

        // A fixed supply at zero volts, or offering zero current, is not a supply. It means these
        // bytes are not a fixed PDO, whatever bits 30-31 claim. Say so rather than rendering a
        // 0V 7.21A power source that does not exist.
        if (voltageMv == 0 || currentMa == 0)
            return Unrecognised(raw, position);

        return new PowerObjectReport
        {
            Kind = "fixed",
            VoltageMillivolts = voltageMv,
            MaxCurrentMilliamps = currentMa,
            MaxPowerMilliwatts = voltageMv * currentMa / 1000,
            UsbCommunicationsCapable = (raw & (1u << 26)) != 0,
            UnconstrainedPower = (raw & (1u << 27)) != 0,
            DualRolePower = (raw & (1u << 29)) != 0,
            Display = $"{Volts(voltageMv)}V at {Amps(currentMa)}A ({Watts(voltageMv * currentMa / 1000)}W)",
            Raw = $"0x{raw:X8}",
            Position = position,
        };
    }

    private static PowerObjectReport Battery(uint raw, int? position)
    {
        int powerMw = (int)(raw & 0x3FF) * 250;
        int minMv = (int)((raw >> 10) & 0x3FF) * 50;
        int maxMv = (int)((raw >> 20) & 0x3FF) * 50;
        if (!Plausible(minMv, maxMv, powerMw)) return Unrecognised(raw, position);

        return new PowerObjectReport
        {
            Kind = "battery",
            MinVoltageMillivolts = minMv,
            MaxVoltageMillivolts = maxMv,
            MaxPowerMilliwatts = powerMw,
            Display = $"battery {Volts(minMv)}-{Volts(maxMv)}V ({Watts(powerMw)}W)",
            Raw = $"0x{raw:X8}",
            Position = position,
        };
    }

    private static PowerObjectReport Variable(uint raw, int? position)
    {
        int currentMa = (int)(raw & 0x3FF) * 10;
        int minMv = (int)((raw >> 10) & 0x3FF) * 50;
        int maxMv = (int)((raw >> 20) & 0x3FF) * 50;
        if (!Plausible(minMv, maxMv, currentMa)) return Unrecognised(raw, position);

        return new PowerObjectReport
        {
            Kind = "variable",
            MinVoltageMillivolts = minMv,
            MaxVoltageMillivolts = maxMv,
            MaxCurrentMilliamps = currentMa,
            MaxPowerMilliwatts = maxMv * currentMa / 1000,
            Display = $"variable {Volts(minMv)}-{Volts(maxMv)}V at {Amps(currentMa)}A",
            Raw = $"0x{raw:X8}",
            Position = position,
        };
    }

    /// <summary>
    /// B29..28 pick the augmented subtype, and each puts different fields in the same bits.
    /// Decoding one with another's layout invents currents, which the cable deduction would then
    /// take as evidence, so subtype 11, which R3.2 reserves, stays unrecognised.
    /// </summary>
    private static PowerObjectReport Augmented(uint raw, int? position) => ((raw >> 28) & 0x3) switch
    {
        0 => Programmable(raw, position),
        1 => EprAdjustable(raw, position),
        2 => SprAdjustable(raw, position),
        _ => Unrecognised(raw, position),
    };

    private static PowerObjectReport Programmable(uint raw, int? position)
    {
        int currentMa = (int)(raw & 0x7F) * 50;
        int minMv = (int)((raw >> 8) & 0xFF) * 100;
        int maxMv = (int)((raw >> 17) & 0xFF) * 100;
        if (!Plausible(minMv, maxMv, currentMa)) return Unrecognised(raw, position);

        // B27, PPS Power Limited: the source cannot deliver the maximum current across the whole
        // range, because its PDP is lower. Which is also why this object's voltage times current,
        // kept below as the product of its fields, is not what the source offers.
        bool limited = (raw & (1u << 27)) != 0;
        return new PowerObjectReport
        {
            Kind = "programmable",
            MinVoltageMillivolts = minMv,
            MaxVoltageMillivolts = maxMv,
            MaxCurrentMilliamps = currentMa,
            MaxPowerMilliwatts = maxMv * currentMa / 1000,
            PowerLimited = limited,
            Display = $"programmable {Volts(minMv)}-{Volts(maxMv)}V at {Amps(currentMa)}A"
                    + (limited ? ", power limited" : ""),
            Raw = $"0x{raw:X8}",
            Position = position,
        };
    }

    /// <summary>
    /// SPR AVS carries two currents and no voltages: the subtype itself defines the ranges, 9-15V
    /// and 15-20V (R3.2 section 6.4.1; Linux's SPR_AVS_TIER constants agree). A zero 15-20V current
    /// is a source that stops at 15V. A zero 9-15V current offers nothing at all.
    /// </summary>
    private static PowerObjectReport SprAdjustable(uint raw, int? position)
    {
        int lowerMa = (int)((raw >> 10) & 0x3FF) * 10;
        int upperMa = (int)(raw & 0x3FF) * 10;
        if (lowerMa == 0) return Unrecognised(raw, position);

        int maxMv = upperMa > 0 ? 20000 : 15000;
        int powerMw = Math.Max(15 * lowerMa, 20 * upperMa);
        return new PowerObjectReport
        {
            Kind = "adjustable",
            ExtendedPowerRange = false,
            MinVoltageMillivolts = 9000,
            MaxVoltageMillivolts = maxMv,
            MaxCurrentMilliamps = Math.Max(lowerMa, upperMa),
            MaxCurrent15To20VoltsMilliamps = upperMa > 0 ? upperMa : null,
            MaxPowerMilliwatts = powerMw,
            Display = upperMa > 0
                ? $"adjustable 9-15V at {Amps(lowerMa)}A, 15-20V at {Amps(upperMa)}A"
                : $"adjustable 9-15V at {Amps(lowerMa)}A",
            Raw = $"0x{raw:X8}",
            Position = position,
        };
    }

    /// <summary>
    /// EPR AVS states a voltage range and a PDP, and no current. Dividing the PDP by a voltage
    /// would produce a current the object never stated, so the current stays null.
    /// </summary>
    private static PowerObjectReport EprAdjustable(uint raw, int? position)
    {
        int pdpMw = (int)(raw & 0xFF) * 1000;
        int minMv = (int)((raw >> 8) & 0xFF) * 100;
        int maxMv = (int)((raw >> 17) & 0x1FF) * 100;
        if (!Plausible(minMv, maxMv, pdpMw)) return Unrecognised(raw, position);

        return new PowerObjectReport
        {
            Kind = "adjustable",
            ExtendedPowerRange = true,
            MinVoltageMillivolts = minMv,
            MaxVoltageMillivolts = maxMv,
            PdpMilliwatts = pdpMw,
            MaxPowerMilliwatts = pdpMw,
            Display = $"EPR adjustable {Volts(minMv)}-{Volts(maxMv)}V up to {Watts(pdpMw)}W",
            Raw = $"0x{raw:X8}",
            Position = position,
        };
    }

    /// <summary>
    /// The most a source offers, for "offers up to", or null when it lists no object that states it.
    ///
    /// Fixed objects, plus the PDP an EPR AVS object states. R3.2 section 10 (Power Rules) sizes a
    /// source by its PDP and has its fixed objects carry it; the PPS, SPR AVS, variable and battery
    /// objects it also lists work within that PDP rather than add to it. The first version took the
    /// largest voltage times current of any object, and a PPS object's maximum voltage times its
    /// maximum current is not an offer: the 100W charger's 3.3-21V at 5A would have read as 105W,
    /// and B27 PPS Power Limited exists precisely because that product can exceed what the source
    /// delivers. Every source lists vSafe5V as a fixed object first (section 6.4.1), so the fixed
    /// objects are always there to count.
    /// </summary>
    public static int? OfferCeilingMilliwatts(IEnumerable<PowerObjectReport> objects)
    {
        int? ceiling = null;
        foreach (PowerObjectReport p in objects)
        {
            int? mw = p.Kind switch
            {
                "fixed" => p.MaxPowerMilliwatts,
                "adjustable" when p.ExtendedPowerRange == true => p.PdpMilliwatts,
                _ => null,
            };
            if (mw is int value && (ceiling is null || value > ceiling)) ceiling = value;
        }
        return ceiling;
    }

    /// <summary>
    /// Decodes the Request Data Object from GET_CONNECTOR_STATUS: what was actually asked for, as
    /// opposed to what the source offers. <paramref name="offered"/> must be the source's list,
    /// which is this PC's own when it is supplying.
    ///
    /// Layouts per PD R3.2 section 6.4.2. In every layout B31..28 is the object position and B26
    /// Capability Mismatch. The rest depends on the kind of object selected:
    ///   fixed, variable  B27 GiveBack, B19..10 operating current, B9..0 maximum operating current,
    ///                    or minimum when GiveBack is set (10 mA)
    ///   battery          B27 GiveBack, B19..10 operating power, B9..0 maximum or minimum (250 mW)
    ///   PPS              B20..9 output voltage (20 mV), B6..0 operating current (50 mA)
    ///   AVS              B20..9 output voltage (25 mV), B6..0 operating current (50 mA)
    ///
    /// The first version read every request as fixed, with a three-bit position. That misread
    /// battery, PPS and AVS requests outright, and folded EPR position 8 onto 0. When the selected
    /// object was not read, or did not decode, which layout applies is unknown, so nothing that
    /// depends on it is decoded.
    /// </summary>
    public static RequestReport? DecodeRequest(uint raw, IReadOnlyList<PowerObjectReport> offered)
    {
        if (raw == 0) return null;

        // Four bits: SPR objects are positions 1-7 and EPR objects 8-13. Linux's RDO_OBJ_POS_MASK
        // is still 0x7, from before EPR existed.
        int position = (int)(raw >> 28) & 0xF;
        bool mismatch = (raw & (1u << 26)) != 0;
        bool giveBack = (raw & (1u << 27)) != 0;
        string hex = $"0x{raw:X8}";

        PowerObjectReport? selected = offered.FirstOrDefault(p => p.Position == position && p.Kind != "unrecognised");

        switch (selected?.Kind)
        {
            case "fixed":
            case "variable":
            {
                int operatingMa = (int)((raw >> 10) & 0x3FF) * 10;
                int lowMa = (int)(raw & 0x3FF) * 10;

                // A variable supply's request names a current but not where in the range the
                // voltage sits, so its power is not knowable from the request.
                int? voltageMv = selected.Kind == "fixed" ? selected.VoltageMillivolts : null;
                int? powerMw = voltageMv is int v ? v * operatingMa / 1000 : null;

                return new RequestReport
                {
                    ObjectPosition = position,
                    SelectedKind = selected.Kind,
                    CapabilityMismatch = mismatch,
                    GiveBack = giveBack,
                    OperatingCurrentMilliamps = operatingMa,
                    MaxOperatingCurrentMilliamps = giveBack ? null : lowMa,
                    MinOperatingCurrentMilliamps = giveBack ? lowMa : null,
                    SelectedVoltageMillivolts = voltageMv,
                    NegotiatedPowerMilliwatts = powerMw,
                    Display = powerMw is int mw
                        ? $"{Volts(voltageMv!.Value)}V at {Amps(operatingMa)}A ({Watts(mw)}W)"
                        : $"variable {Volts(selected.MinVoltageMillivolts ?? 0)}-{Volts(selected.MaxVoltageMillivolts ?? 0)}V "
                        + $"at {Amps(operatingMa)}A, voltage within the range not stated",
                    Raw = hex,
                };
            }

            case "battery":
            {
                int operatingMw = (int)((raw >> 10) & 0x3FF) * 250;
                int lowMw = (int)(raw & 0x3FF) * 250;
                return new RequestReport
                {
                    ObjectPosition = position,
                    SelectedKind = selected.Kind,
                    CapabilityMismatch = mismatch,
                    GiveBack = giveBack,
                    OperatingPowerMilliwatts = operatingMw,
                    MaxOperatingPowerMilliwatts = giveBack ? null : lowMw,
                    MinOperatingPowerMilliwatts = giveBack ? lowMw : null,
                    NegotiatedPowerMilliwatts = operatingMw,
                    Display = $"battery {Volts(selected.MinVoltageMillivolts ?? 0)}-{Volts(selected.MaxVoltageMillivolts ?? 0)}V "
                            + $"at {Watts(operatingMw)}W",
                    Raw = hex,
                };
            }

            case "programmable":
            case "adjustable":
            {
                // B27 is reserved in these layouts, so GiveBack is not reported. B20..9 is twelve
                // bits in R3.2; Linux's RDO_PROG_VOLT_MASK is eleven, from PD 3.0, which gives the
                // same answer for any SPR voltage.
                bool pps = selected.Kind == "programmable";
                int outputMv = (int)((raw >> 9) & 0xFFF) * (pps ? 20 : 25);
                int operatingMa = (int)(raw & 0x7F) * 50;
                int powerMw = outputMv * operatingMa / 1000;
                return new RequestReport
                {
                    ObjectPosition = position,
                    SelectedKind = selected.Kind,
                    CapabilityMismatch = mismatch,
                    OutputVoltageMillivolts = outputMv,
                    OperatingCurrentMilliamps = operatingMa,
                    NegotiatedPowerMilliwatts = powerMw,
                    Display = $"{(pps ? "PPS" : "AVS")} {Volts(outputMv)}V at {Amps(operatingMa)}A ({Watts(powerMw)}W)",
                    Raw = hex,
                };
            }

            default:
                return new RequestReport
                {
                    ObjectPosition = position,
                    CapabilityMismatch = mismatch,
                    Display = $"PDO {position}, not decoded because that object was not read or not recognised",
                    Raw = hex,
                };
        }
    }

    private static string Volts(int millivolts) => (millivolts / 1000.0).ToString("0.##");
    private static string Amps(int milliamps) => (milliamps / 1000.0).ToString("0.##");
    private static string Watts(int milliwatts) => (milliwatts / 1000.0).ToString("0.#");
}
