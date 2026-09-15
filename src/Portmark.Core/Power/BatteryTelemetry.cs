using System.Diagnostics;
using Portmark.Core.Model;
using Portmark.Core.Native;

namespace Portmark.Core.Power;

/// <summary>
/// What the batteries say together: the one reading that can tell a machine running down on a
/// small contract from one that is simply full. Null wherever the batteries did not say.
/// </summary>
public sealed record BatteryFlow(int? RateMilliwatts, bool OnExternalPower, bool Discharging, bool Charging,
                                 int? ChargePercent);

/// <summary>Two capacity readings of one battery and the time between them.</summary>
public sealed class BatterySample
{
    public int Index { get; init; }
    public int? StartMilliwattHours { get; init; }
    public int? EndMilliwattHours { get; init; }
    public int? ChangeMilliwattHours { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>The battery's average net charge flow over the interval. Negative while losing charge.</summary>
    public int? AverageNetMilliwatts { get; init; }
    public string? Note { get; init; }
    public BatteryReport? Start { get; init; }
    public BatteryReport? End { get; init; }
}

/// <summary>
/// Decodes the battery class driver's BATTERY_INFORMATION and BATTERY_STATUS structures, as
/// documented on Microsoft Learn, and reads them live through <see cref="BatteryDevices"/>.
///
/// This exists because the percentage alone was not enough. On the machine it was written for, a
/// 15W contract held against a 100W supply while the battery fell from 46 to 30 percent, Windows
/// showed it as charging, and the WMI charge rate read zero. The percentage could rule out a full
/// battery; only the battery's own rate can say it is losing charge.
///
/// Decoding is kept apart from the IOCTLs so every sentinel can be tested from raw bytes.
/// </summary>
public static class BatteryTelemetry
{
    // BATTERY_INFORMATION.Capabilities
    public const uint SystemBatteryFlag = 0x80000000;
    public const uint CapacityRelativeFlag = 0x40000000;
    public const uint ShortTermFlag = 0x20000000;

    // BATTERY_STATUS.PowerState
    public const uint PowerOnLineFlag = 0x00000001;
    public const uint DischargingFlag = 0x00000002;
    public const uint ChargingFlag = 0x00000004;
    public const uint CriticalFlag = 0x00000008;

    // Sentinels from poclass.h. The rate's is 0x80000000 read as a signed LONG.
    public const uint UnknownCapacity = 0xFFFFFFFF;
    public const uint UnknownVoltage = 0xFFFFFFFF;
    public const int UnknownRate = int.MinValue;

    /// <summary>sizeof(BATTERY_INFORMATION): Capabilities, Technology, Reserved[3], Chemistry[4], five ULONGs.</summary>
    public const int InformationLength = 32;

    /// <summary>sizeof(BATTERY_STATUS): PowerState, Capacity, Voltage, Rate.</summary>
    public const int StatusLength = 16;

    public const string CoarseReportingNote =
        "Battery capacity reporting is coarse: the fuel gauge reports it in steps and may not update "
      + "every second, so a short sample can show no change, or a whole step at once. This is the "
      + "average of the battery's net charge flow over the interval, not the power arriving through "
      + "the cable.";

    /// <summary>
    /// A battery that answered the tag query. Either structure may be missing or short, and a
    /// missing structure leaves its fields null rather than decoded from zeros.
    /// </summary>
    public static BatteryReport Decode(int index, byte[]? information, byte[]? status,
                                       string? deviceName = null, string? manufacturer = null, string? reason = null)
    {
        bool haveInformation = information is { Length: >= InformationLength };
        bool haveStatus = status is { Length: >= StatusLength };

        uint capabilities = haveInformation ? BitConverter.ToUInt32(information!, 0) : 0;
        bool relative = (capabilities & CapacityRelativeFlag) != 0;

        // Units are only known once the information block says they are not relative. Without
        // it a capacity of 40 could be 40 mWh or 40 percent, so neither is claimed.
        bool absolute = haveInformation && !relative;

        uint? designed = haveInformation ? Known(BitConverter.ToUInt32(information!, 12)) : null;
        uint? fullCharged = haveInformation ? Known(BitConverter.ToUInt32(information!, 16)) : null;
        uint cycles = haveInformation ? BitConverter.ToUInt32(information!, 28) : 0;

        uint? remaining = haveStatus ? Known(BitConverter.ToUInt32(status!, 4)) : null;
        uint voltageRaw = haveStatus ? BitConverter.ToUInt32(status!, 8) : UnknownVoltage;
        int rateRaw = haveStatus ? BitConverter.ToInt32(status!, 12) : UnknownRate;

        BatteryPowerStateReport? powerState = null;
        if (haveStatus)
        {
            uint flags = BitConverter.ToUInt32(status!, 0);
            powerState = new BatteryPowerStateReport
            {
                Flags = (int)flags,
                OnExternalPower = (flags & PowerOnLineFlag) != 0,
                Discharging = (flags & DischargingFlag) != 0,
                Charging = (flags & ChargingFlag) != 0,
                Critical = (flags & CriticalFlag) != 0,
            };
        }

        // Microsoft defines the gas gauge as Capacity over FullChargedCapacity in whatever units
        // both are in, so the percentage survives a relative battery.
        int? chargePercent = haveInformation && remaining is uint r && fullCharged is uint f && f > 0
            ? (int)Math.Round(r * 100.0 / f)
            : null;

        int? healthPercent = absolute && designed is uint d && d > 0 && fullCharged is uint full
            ? (int)Math.Round(full * 100.0 / d)
            : null;

        string? note = relative
            ? "This battery reports capacity and rate in relative units, so no milliwatt-hour or milliwatt "
            + "figures are given. The charge percentage is still its own."
            : !haveInformation && haveStatus
                ? "The battery's information block could not be read, so whether its capacity and rate are in "
                + "milliwatt-hours and milliwatts is unknown, and none are given."
                : null;

        return new BatteryReport
        {
            Index = index,
            Present = true,
            DeviceName = deviceName,
            Manufacturer = manufacturer,
            Chemistry = haveInformation ? Chemistry(information!.AsSpan(8, 4)) : null,
            IsSystemBattery = haveInformation ? (capabilities & SystemBatteryFlag) != 0 : null,
            IsShortTerm = haveInformation ? (capabilities & ShortTermFlag) != 0 : null,
            CapacityRelative = relative,
            PowerState = powerState,
            RemainingCapacityMilliwattHours = absolute ? ToInt(remaining) : null,
            FullChargeCapacityMilliwattHours = absolute ? ToInt(fullCharged) : null,
            DesignCapacityMilliwattHours = absolute ? ToInt(designed) : null,
            RateMilliwatts = absolute && rateRaw != UnknownRate ? rateRaw : null,
            VoltageMillivolts = voltageRaw == UnknownVoltage ? null : ToInt(voltageRaw),
            ChargePercent = chargePercent,
            HealthPercent = healthPercent,
            HealthNote = healthPercent is null
                ? null
                : "The battery's own estimate: its fuel gauge's full-charge capacity against its design "
                + "capacity. Nothing here measured it.",
            CycleCount = cycles == 0 ? null : ToInt(cycles),
            Note = note,
            Reason = reason,
            Raw = new BatteryRawReport
            {
                InformationHex = information is null ? null : Convert.ToHexString(information),
                StatusHex = status is null ? null : Convert.ToHexString(status),
            },
        };
    }

    /// <summary>A battery device with no battery in it. Devices are slots, not batteries.</summary>
    public static BatteryReport Absent(int index, string reason) => new()
    {
        Index = index,
        Present = false,
        Reason = reason,
    };

    /// <summary>
    /// The system batteries taken together, or null when none answered with a status, which is
    /// what lets the diagnostic fall back to GetSystemPowerStatus. A UPS is left out: its drain
    /// says nothing about what this PC is drawing.
    /// </summary>
    public static BatteryFlow? Summarise(IEnumerable<BatteryReport> batteries)
    {
        List<BatteryReport> system = batteries
            .Where(b => b.Present && b.PowerState is not null && b.IsSystemBattery == true && b.IsShortTerm != true)
            .ToList();
        if (system.Count == 0) return null;

        // One unknown rate makes the total unknown. Adding the rest would understate it.
        int? rate = system.All(b => b.RateMilliwatts is not null) ? system.Sum(b => b.RateMilliwatts!.Value) : null;

        int? percent = null;
        if (system.All(b => b.RemainingCapacityMilliwattHours is not null && b.FullChargeCapacityMilliwattHours is not null))
        {
            long full = system.Sum(b => (long)b.FullChargeCapacityMilliwattHours!.Value);
            long remaining = system.Sum(b => (long)b.RemainingCapacityMilliwattHours!.Value);
            if (full > 0) percent = (int)Math.Round(remaining * 100.0 / full);
        }
        else if (system.Count == 1)
        {
            percent = system[0].ChargePercent;
        }

        return new BatteryFlow(
            rate,
            system.Any(b => b.PowerState!.OnExternalPower),
            system.Any(b => b.PowerState!.Discharging),
            system.Any(b => b.PowerState!.Charging),
            percent);
    }

    /// <summary>The average net charge flow between two readings of the same battery.</summary>
    public static BatterySample Compare(BatteryReport start, BatteryReport end, TimeSpan elapsed)
    {
        int? change = null, average = null;
        string? note;

        if (start.Raw.Tag != end.Raw.Tag)
            note = "The battery tag changed during the sample, which Windows does when the battery or its "
                 + "static data changes, so the two readings are not compared.";
        else if (start.RemainingCapacityMilliwattHours is not int a || end.RemainingCapacityMilliwattHours is not int b)
            note = "The battery did not report its remaining capacity in milliwatt-hours at both ends of the "
                 + "sample, so no average could be worked out.";
        else if (elapsed <= TimeSpan.Zero)
            note = "No time passed between the readings, so no average could be worked out.";
        else
        {
            change = b - a;
            average = (int)Math.Round(change.Value * 3_600_000.0 / elapsed.TotalMilliseconds);
            note = change == 0
                ? "The reported capacity did not change. Capacity is reported in steps, so this is not "
                + "evidence that no charge moved."
                : null;
        }

        return new BatterySample
        {
            Index = start.Index,
            StartMilliwattHours = start.RemainingCapacityMilliwattHours,
            EndMilliwattHours = end.RemainingCapacityMilliwattHours,
            ChangeMilliwattHours = change,
            Elapsed = elapsed,
            AverageNetMilliwatts = average,
            Note = note,
            Start = start,
            End = end,
        };
    }

    /// <summary>Reads every battery device now. Read-only: the tag, information and status queries.</summary>
    public static List<BatteryReport> ReadAll(out string? note)
    {
        IReadOnlyList<BatteryDeviceReading> readings = BatteryDevices.ReadAll(out int error);

        note = readings.Count > 0
            ? null
            : error != 0
                ? $"The battery devices could not be listed: {Win32.Describe(error)}"
                : "Windows lists no battery devices on this PC, so there is no battery reading.";

        var batteries = new List<BatteryReport>();
        for (int i = 0; i < readings.Count; i++)
        {
            BatteryDeviceReading reading = readings[i];
            if (reading.Absent)
            {
                batteries.Add(Absent(i + 1, "No battery is in this battery slot."));
                continue;
            }

            if (reading.Tag is null)
            {
                batteries.Add(new BatteryReport { Index = i + 1, Present = false, Reason = reading.Error });
                continue;
            }

            BatteryReport report = Decode(i + 1, reading.Information, reading.Status,
                                          reading.DeviceName, reading.ManufacturerName, reading.Error);
            report.Raw.Tag = reading.Tag;
            batteries.Add(report);
        }

        return batteries;
    }

    /// <summary>
    /// Reads remaining capacity, waits, and reads it again. Battery IOCTLs only: no port controller
    /// is touched, so this is safe to run for as long as asked.
    /// </summary>
    public static List<BatterySample> Sample(TimeSpan interval, out string? note)
    {
        List<BatteryReport> start = ReadAll(out note);
        var clock = Stopwatch.StartNew();
        Thread.Sleep(interval);
        List<BatteryReport> end = ReadAll(out _);
        TimeSpan elapsed = clock.Elapsed;

        var samples = new List<BatterySample>();
        foreach (BatteryReport first in start.Where(b => b.Present))
        {
            BatteryReport? last = end.FirstOrDefault(b => b.Index == first.Index);
            samples.Add(last is null
                ? new BatterySample
                {
                    Index = first.Index, Elapsed = elapsed, Start = first,
                    StartMilliwattHours = first.RemainingCapacityMilliwattHours,
                    Note = "The battery did not answer at the end of the sample.",
                }
                : Compare(first, last, elapsed));
        }

        return samples;
    }

    private static uint? Known(uint value) => value == UnknownCapacity ? null : value;

    private static int? ToInt(uint? value) => value is uint v && v <= int.MaxValue ? (int)v : null;

    /// <summary>Four bytes, not necessarily terminated. Anything unprintable is not a chemistry.</summary>
    private static string? Chemistry(ReadOnlySpan<byte> bytes)
    {
        var chars = new List<char>();
        foreach (byte b in bytes)
        {
            if (b == 0) break;
            if (b < 0x20 || b > 0x7E) return null;
            chars.Add((char)b);
        }

        string text = new string(chars.ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }
}
