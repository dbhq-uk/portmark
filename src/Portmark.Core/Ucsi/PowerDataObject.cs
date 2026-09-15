using Portmark.Core.Model;

namespace Portmark.Core.Ucsi;

/// <summary>
/// Decodes USB Power Delivery Power Data Objects, as returned by UCSI GET_PDOS.
///
/// A PDO is 32 bits. Bits 30-31 select the shape of the rest:
///   00 Fixed supply     bits 0-9 max current (10 mA), bits 10-19 voltage (50 mV)
///   01 Battery          bits 0-9 max power (250 mW), 10-19 min voltage, 20-29 max voltage
///   10 Variable supply  bits 0-9 max current (10 mA), 10-19 min voltage, 20-29 max voltage
///   11 Augmented (PPS)  bits 0-6 max current (50 mA), 8-15 min voltage (100 mV),
///                       17-24 max voltage (100 mV)
///
/// Verified against a 65W charger, which decoded to 5V/3A, 9V/3A, 15V/3A and 20V/3.25A: exactly
/// the profile printed on the supply.
/// </summary>
public static class PowerDataObject
{
    public static List<PowerObjectReport> DecodeAll(ReadOnlySpan<byte> data)
    {
        var results = new List<PowerObjectReport>();
        for (int offset = 0; offset + 4 <= data.Length; offset += 4)
        {
            uint raw = BitConverter.ToUInt32(data.Slice(offset, 4));
            if (raw == 0) continue;   // an empty slot, not a supply offering nothing
            results.Add(Decode(raw));
        }
        return results;
    }

    /// <summary>A structurally impossible object, reported as such rather than decoded.</summary>
    private static PowerObjectReport Unrecognised(uint raw) => new()
    {
        Kind = "unrecognised",
        Display = "unrecognised power object",
        Raw = $"0x{raw:X8}",
    };

    public static PowerObjectReport Decode(uint raw)
    {
        int kind = (int)(raw >> 30) & 0x03;
        return kind switch
        {
            0 => Fixed(raw),
            1 => Battery(raw),
            2 => Variable(raw),
            _ => Augmented(raw),
        };
    }

    private static PowerObjectReport Fixed(uint raw)
    {
        int currentMa = (int)(raw & 0x3FF) * 10;
        int voltageMv = (int)((raw >> 10) & 0x3FF) * 50;

        // A fixed supply at zero volts, or offering zero current, is not a supply. It means these
        // bytes are not a fixed PDO, whatever bits 30-31 claim. Say so rather than rendering a
        // 0V 7.21A power source that does not exist.
        if (voltageMv == 0 || currentMa == 0)
            return Unrecognised(raw);

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
        };
    }

    private static PowerObjectReport Battery(uint raw)
    {
        int powerMw = (int)(raw & 0x3FF) * 250;
        int minMv = (int)((raw >> 10) & 0x3FF) * 50;
        int maxMv = (int)((raw >> 20) & 0x3FF) * 50;
        return new PowerObjectReport
        {
            Kind = "battery",
            MinVoltageMillivolts = minMv,
            MaxVoltageMillivolts = maxMv,
            MaxPowerMilliwatts = powerMw,
            Display = $"battery {Volts(minMv)}-{Volts(maxMv)}V ({Watts(powerMw)}W)",
            Raw = $"0x{raw:X8}",
        };
    }

    private static PowerObjectReport Variable(uint raw)
    {
        int currentMa = (int)(raw & 0x3FF) * 10;
        int minMv = (int)((raw >> 10) & 0x3FF) * 50;
        int maxMv = (int)((raw >> 20) & 0x3FF) * 50;
        return new PowerObjectReport
        {
            Kind = "variable",
            MinVoltageMillivolts = minMv,
            MaxVoltageMillivolts = maxMv,
            MaxCurrentMilliamps = currentMa,
            MaxPowerMilliwatts = maxMv * currentMa / 1000,
            Display = $"variable {Volts(minMv)}-{Volts(maxMv)}V at {Amps(currentMa)}A",
            Raw = $"0x{raw:X8}",
        };
    }

    private static PowerObjectReport Augmented(uint raw)
    {
        // Bits 28-29 pick the augmented subtype. Only 00, SPR PPS, has the layout below. The
        // others (AVS, reserved) put different fields in the same bits, and decoding them as PPS
        // invents currents, which the cable deduction would then take as evidence.
        if (((raw >> 28) & 0x3) != 0)
            return Unrecognised(raw);

        int currentMa = (int)(raw & 0x7F) * 50;
        int minMv = (int)((raw >> 8) & 0xFF) * 100;
        int maxMv = (int)((raw >> 17) & 0xFF) * 100;
        return new PowerObjectReport
        {
            Kind = "programmable",
            MinVoltageMillivolts = minMv,
            MaxVoltageMillivolts = maxMv,
            MaxCurrentMilliamps = currentMa,
            MaxPowerMilliwatts = maxMv * currentMa / 1000,
            Display = $"programmable {Volts(minMv)}-{Volts(maxMv)}V at {Amps(currentMa)}A",
            Raw = $"0x{raw:X8}",
        };
    }

    /// <summary>
    /// Decodes the Request Data Object from GET_CONNECTOR_STATUS: what was actually asked for, as
    /// opposed to what the supply offers. Object position selects which PDO was chosen, counting
    /// from one.
    /// </summary>
    public static RequestReport? DecodeRequest(uint raw, IReadOnlyList<PowerObjectReport> offered)
    {
        if (raw == 0) return null;

        int position = (int)((raw >> 28) & 0x07);
        int maxCurrentMa = (int)(raw & 0x3FF) * 10;
        int operatingMa = (int)((raw >> 10) & 0x3FF) * 10;

        PowerObjectReport? selected = position >= 1 && position <= offered.Count
            ? offered[position - 1]
            : null;

        return new RequestReport
        {
            ObjectPosition = position,
            OperatingCurrentMilliamps = operatingMa,
            MaxOperatingCurrentMilliamps = maxCurrentMa,
            SelectedVoltageMillivolts = selected?.VoltageMillivolts,
            NegotiatedPowerMilliwatts = selected?.VoltageMillivolts is int mv
                ? mv * operatingMa / 1000
                : null,
            Display = selected?.VoltageMillivolts is int v
                ? $"{Volts(v)}V at {Amps(operatingMa)}A ({Watts(v * operatingMa / 1000)}W)"
                : $"PDO {position} at {Amps(operatingMa)}A",
            Raw = $"0x{raw:X8}",
        };
    }

    private static string Volts(int millivolts) => (millivolts / 1000.0).ToString("0.##");
    private static string Amps(int milliamps) => (milliamps / 1000.0).ToString("0.##");
    private static string Watts(int milliwatts) => (milliwatts / 1000.0).ToString("0.#");
}
