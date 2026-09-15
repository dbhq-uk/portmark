using Portmark.Core.Model;
using Portmark.Core.Power;
using Portmark.Core.Ucsi;
using Xunit;

namespace Portmark.Core.Tests;

/// <summary>
/// Builds the two structures the battery class driver returns, field by field, in the layout
/// Microsoft documents for BATTERY_INFORMATION and BATTERY_STATUS.
/// </summary>
internal static class BatteryVectors
{
    public static byte[] Information(uint capabilities = BatteryTelemetry.SystemBatteryFlag, string chemistry = "LION",
                                     uint designed = 52_500, uint fullCharged = 47_880, uint cycles = 0,
                                     uint criticalBias = 0)
    {
        // Capabilities, Technology, Reserved[3], Chemistry[4], DesignedCapacity,
        // FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount: 36 bytes.
        var bytes = new byte[36];
        BitConverter.GetBytes(capabilities).CopyTo(bytes, 0);
        bytes[4] = 1;   // Technology: rechargeable
        for (int i = 0; i < 4 && i < chemistry.Length; i++) bytes[8 + i] = (byte)chemistry[i];
        BitConverter.GetBytes(designed).CopyTo(bytes, 12);
        BitConverter.GetBytes(fullCharged).CopyTo(bytes, 16);
        BitConverter.GetBytes(criticalBias).CopyTo(bytes, 28);
        BitConverter.GetBytes(cycles).CopyTo(bytes, 32);
        return bytes;
    }

    public static byte[] Status(uint powerState, uint capacity, uint voltage, int rate)
    {
        var bytes = new byte[BatteryTelemetry.StatusLength];
        BitConverter.GetBytes(powerState).CopyTo(bytes, 0);
        BitConverter.GetBytes(capacity).CopyTo(bytes, 4);
        BitConverter.GetBytes(voltage).CopyTo(bytes, 8);
        BitConverter.GetBytes(rate).CopyTo(bytes, 12);
        return bytes;
    }

    public const uint OnLineDischarging = BatteryTelemetry.PowerOnLineFlag | BatteryTelemetry.DischargingFlag;
}

/// <summary>
/// The battery's own readings, decoded from the raw structures. Every unknown sentinel becomes
/// null, and nothing measured in relative units is ever presented as milliwatts.
/// </summary>
public class BatteryTelemetryTests
{
    [Fact]
    public void AnAbsoluteBatteryDecodesToMilliwattHoursAndMilliwatts()
    {
        BatteryReport b = BatteryTelemetry.Decode(1,
            BatteryVectors.Information(),
            BatteryVectors.Status(BatteryVectors.OnLineDischarging, 15_720, 15_212, -8_214));

        Assert.True(b.Present);
        Assert.Equal("LION", b.Chemistry);
        Assert.Equal(52_500, b.DesignCapacityMilliwattHours);
        Assert.Equal(47_880, b.FullChargeCapacityMilliwattHours);
        Assert.Equal(15_720, b.RemainingCapacityMilliwattHours);
        Assert.Equal(-8_214, b.RateMilliwatts);
        Assert.Equal(15_212, b.VoltageMillivolts);
        Assert.Equal(33, b.ChargePercent);
        Assert.False(b.CapacityRelative);
        Assert.True(b.IsSystemBattery);
    }

    [Fact]
    public void PowerStateFlagsAreEachReported()
    {
        BatteryReport b = BatteryTelemetry.Decode(1, BatteryVectors.Information(),
            BatteryVectors.Status(0x0F, 1_000, 11_000, -1));

        BatteryPowerStateReport state = Assert.IsType<BatteryPowerStateReport>(b.PowerState);
        Assert.True(state.OnExternalPower);
        Assert.True(state.Discharging);
        Assert.True(state.Charging);
        Assert.True(state.Critical);
        Assert.Equal(0x0F, state.Flags);
    }

    [Fact]
    public void NoFlagsSetMeansNoFlagsClaimed()
    {
        BatteryReport b = BatteryTelemetry.Decode(1, BatteryVectors.Information(),
            BatteryVectors.Status(0, 1_000, 11_000, 0));

        BatteryPowerStateReport state = Assert.IsType<BatteryPowerStateReport>(b.PowerState);
        Assert.False(state.OnExternalPower);
        Assert.False(state.Discharging);
        Assert.False(state.Charging);
        Assert.False(state.Critical);
    }

    [Fact]
    public void UnknownSentinelsBecomeNull()
    {
        // BATTERY_UNKNOWN_CAPACITY and BATTERY_UNKNOWN_VOLTAGE are 0xFFFFFFFF, and
        // BATTERY_UNKNOWN_RATE is 0x80000000. None of them is a reading.
        BatteryReport b = BatteryTelemetry.Decode(1,
            BatteryVectors.Information(designed: 0xFFFFFFFF, fullCharged: 0xFFFFFFFF),
            BatteryVectors.Status(BatteryTelemetry.PowerOnLineFlag, 0xFFFFFFFF, 0xFFFFFFFF, int.MinValue));

        Assert.Null(b.RemainingCapacityMilliwattHours);
        Assert.Null(b.FullChargeCapacityMilliwattHours);
        Assert.Null(b.DesignCapacityMilliwattHours);
        Assert.Null(b.VoltageMillivolts);
        Assert.Null(b.RateMilliwatts);
        Assert.Null(b.ChargePercent);
        Assert.Null(b.HealthPercent);
        Assert.NotNull(b.PowerState);
    }

    [Fact]
    public void ARelativeBatteryReportsNoMilliwattsAtAll()
    {
        // BATTERY_CAPACITY_RELATIVE: capacity and rate are in arbitrary units, so a rate of 200
        // is not 200 mW. The percentage survives, because Microsoft defines it as capacity over
        // full-charged capacity in whatever units both are in.
        BatteryReport b = BatteryTelemetry.Decode(1,
            BatteryVectors.Information(BatteryTelemetry.SystemBatteryFlag | BatteryTelemetry.CapacityRelativeFlag,
                                       designed: 100, fullCharged: 100),
            BatteryVectors.Status(BatteryVectors.OnLineDischarging, 40, 12_000, -200));

        Assert.True(b.CapacityRelative);
        Assert.Null(b.RemainingCapacityMilliwattHours);
        Assert.Null(b.FullChargeCapacityMilliwattHours);
        Assert.Null(b.DesignCapacityMilliwattHours);
        Assert.Null(b.RateMilliwatts);
        Assert.Equal(40, b.ChargePercent);
        Assert.Equal(12_000, b.VoltageMillivolts);
        Assert.Contains("relative", b.Note);
    }

    [Fact]
    public void HealthIsFullChargeOverDesign_AndSaysWhoseEstimateItIs()
    {
        BatteryReport b = BatteryTelemetry.Decode(1, BatteryVectors.Information(designed: 52_500, fullCharged: 47_880),
                                                  BatteryVectors.Status(0, 1_000, 11_000, 0));

        Assert.Equal(91, b.HealthPercent);
        Assert.Contains("battery's own estimate", b.HealthNote);
    }

    [Fact]
    public void AZeroDesignCapacityGivesNoHealth()
    {
        BatteryReport b = BatteryTelemetry.Decode(1, BatteryVectors.Information(designed: 0, fullCharged: 40_000),
                                                  BatteryVectors.Status(0, 1_000, 11_000, 0));

        Assert.Null(b.HealthPercent);
        Assert.Null(b.HealthNote);
    }

    [Fact]
    public void WithoutTheInformationBlockNoCapacityIsTrusted()
    {
        // Whether the units are relative is in BATTERY_INFORMATION. Without it, a capacity of 40
        // could be 40 mWh or 40 percent, so neither is claimed.
        BatteryReport b = BatteryTelemetry.Decode(1, null,
            BatteryVectors.Status(BatteryVectors.OnLineDischarging, 40, 12_000, -200), reason: "information failed");

        Assert.Null(b.RemainingCapacityMilliwattHours);
        Assert.Null(b.RateMilliwatts);
        Assert.Null(b.ChargePercent);
        Assert.Equal(12_000, b.VoltageMillivolts);
        Assert.NotNull(b.PowerState);
    }

    [Fact]
    public void ShortBuffersAreNotDecodedIntoFields()
    {
        BatteryReport b = BatteryTelemetry.Decode(1, new byte[8], new byte[4]);

        Assert.Null(b.PowerState);
        Assert.Null(b.Chemistry);
        Assert.Null(b.DesignCapacityMilliwattHours);
        Assert.Null(b.VoltageMillivolts);
    }

    [Fact]
    public void TheRawBytesAreKept()
    {
        byte[] info = BatteryVectors.Information();
        byte[] status = BatteryVectors.Status(1, 2, 3, 4);
        BatteryReport b = BatteryTelemetry.Decode(1, info, status);

        Assert.Equal(Convert.ToHexString(info), b.Raw.InformationHex);
        Assert.Equal(Convert.ToHexString(status), b.Raw.StatusHex);
    }

    [Fact]
    public void TheCapturedThinkPadBatteryDecodes()
    {
        // Captured on the ThinkPad T16 Gen 2 (AMD), 100W supply attached, battery reporting full.
        // The driver returned 36 bytes, which is sizeof(BATTERY_INFORMATION). The first version
        // miscounted the structure as 32 bytes, so its 32-byte buffer was refused with
        // ERROR_INSUFFICIENT_BUFFER, and it then read CriticalBias (zero here) as the cycle count
        // and called the final 0x78 undocumented. That final ULONG is CycleCount: 120.
        // The design capacity and cycle count have not been checked against the battery's label.
        BatteryReport b = BatteryTelemetry.Decode(1,
            Convert.FromHexString("00000080010000004C695000F04F010090140100C8000000D40D00000000000078000000"),
            Convert.FromHexString("050000007C14010075440000EB180000"),
            "5B11M90038", "Celxpert");

        Assert.Equal("LiP", b.Chemistry);
        Assert.True(b.IsSystemBattery);
        Assert.False(b.CapacityRelative);
        Assert.Equal(86_000, b.DesignCapacityMilliwattHours);
        Assert.Equal(70_800, b.FullChargeCapacityMilliwattHours);
        Assert.Equal(70_780, b.RemainingCapacityMilliwattHours);
        Assert.Equal(6_379, b.RateMilliwatts);
        Assert.Equal(17_525, b.VoltageMillivolts);
        Assert.Equal(100, b.ChargePercent);
        Assert.Equal(82, b.HealthPercent);
        Assert.Equal(120, b.CycleCount);

        BatteryPowerStateReport state = Assert.IsType<BatteryPowerStateReport>(b.PowerState);
        Assert.True(state.OnExternalPower);
        Assert.True(state.Charging);
        Assert.False(state.Discharging);
    }

    [Fact]
    public void TheCycleCountIsTheLastUlong_NotCriticalBias()
    {
        BatteryReport b = BatteryTelemetry.Decode(1, BatteryVectors.Information(cycles: 57, criticalBias: 300),
                                                  BatteryVectors.Status(0, 1_000, 11_000, 0));

        Assert.Equal(36, BatteryTelemetry.InformationLength);
        Assert.Equal(57, b.CycleCount);
    }

    [Fact]
    public void AZeroCycleCountMeansNoCycleCounter()
    {
        // Microsoft: "If the battery does not support a cycle counter, this member is zero."
        BatteryReport b = BatteryTelemetry.Decode(1, BatteryVectors.Information(cycles: 0, criticalBias: 300),
                                                  BatteryVectors.Status(0, 1_000, 11_000, 0));

        Assert.Null(b.CycleCount);
    }

    [Fact]
    public void AThirtyTwoByteInformationBlockIsShortAndNotDecoded()
    {
        byte[] truncated = BatteryVectors.Information(cycles: 57)[..32];
        BatteryReport b = BatteryTelemetry.Decode(1, truncated, BatteryVectors.Status(0, 1_000, 11_000, 0));

        Assert.Null(b.CycleCount);
        Assert.Null(b.DesignCapacityMilliwattHours);
        Assert.Null(b.IsSystemBattery);
    }

    [Fact]
    public void ChemistryIsTrimmedAndUnprintableChemistryIsNull()
    {
        Assert.Equal("RAM", BatteryTelemetry.Decode(1, BatteryVectors.Information(chemistry: "RAM\0"), null).Chemistry);
        Assert.Null(BatteryTelemetry.Decode(1, BatteryVectors.Information(chemistry: "\0\0\0\0"), null).Chemistry);
    }

    [Fact]
    public void AnEmptySlotIsNotABattery()
    {
        BatteryReport b = BatteryTelemetry.Absent(2, "No battery is in this slot.");

        Assert.False(b.Present);
        Assert.Null(b.PowerState);
        Assert.Null(BatteryTelemetry.Summarise([b]));
    }
}

/// <summary>What the batteries say together, for the charge diagnostic.</summary>
public class BatterySummaryTests
{
    private static BatteryReport Battery(uint state, uint capacity, int rate, uint full = 50_000,
                                         uint capabilities = BatteryTelemetry.SystemBatteryFlag)
        => BatteryTelemetry.Decode(1, BatteryVectors.Information(capabilities, fullCharged: full),
                                   BatteryVectors.Status(state, capacity, 12_000, rate));

    [Fact]
    public void NoBatteriesGiveNoSummary()
    {
        Assert.Null(BatteryTelemetry.Summarise([]));
    }

    [Fact]
    public void TwoBatteriesAreAddedTogether()
    {
        BatteryFlow flow = Assert.IsType<BatteryFlow>(BatteryTelemetry.Summarise([
            Battery(BatteryVectors.OnLineDischarging, 10_000, -3_000),
            Battery(BatteryTelemetry.PowerOnLineFlag, 30_000, -1_000),
        ]));

        Assert.Equal(-4_000, flow.RateMilliwatts);
        Assert.True(flow.OnExternalPower);
        Assert.True(flow.Discharging);
        Assert.Equal(40, flow.ChargePercent);
    }

    [Fact]
    public void OneUnknownRateMakesTheTotalUnknown()
    {
        BatteryFlow flow = Assert.IsType<BatteryFlow>(BatteryTelemetry.Summarise([
            Battery(BatteryVectors.OnLineDischarging, 10_000, -3_000),
            Battery(BatteryTelemetry.PowerOnLineFlag, 30_000, int.MinValue),
        ]));

        Assert.Null(flow.RateMilliwatts);
    }

    [Fact]
    public void AnUpsIsNotTheSystemBattery()
    {
        // A battery without BATTERY_SYSTEM_BATTERY, or marked BATTERY_IS_SHORT_TERM, is not what
        // runs this PC, and its drain says nothing about the USB-C contract.
        Assert.Null(BatteryTelemetry.Summarise([Battery(BatteryVectors.OnLineDischarging, 10_000, -3_000, capabilities: 0)]));
        Assert.Null(BatteryTelemetry.Summarise([Battery(BatteryVectors.OnLineDischarging, 10_000, -3_000,
            capabilities: BatteryTelemetry.SystemBatteryFlag | BatteryTelemetry.ShortTermFlag)]));
    }

    [Fact]
    public void EveryBatteryReadMeansNoCoverageNote()
    {
        BatteryFlow flow = Assert.IsType<BatteryFlow>(BatteryTelemetry.Summarise([
            Battery(BatteryVectors.OnLineDischarging, 10_000, -3_000),
            Battery(BatteryTelemetry.PowerOnLineFlag, 30_000, -1_000),
        ]));

        Assert.Null(flow.CoverageNote);
    }

    [Fact]
    public void ASystemBatteryWithoutStatusMakesTheTotalUnknown()
    {
        // The first version dropped a battery without a status before adding up, so the one
        // battery that answered had its rate and percentage reported as the whole machine's.
        BatteryReport unread = BatteryTelemetry.Decode(2, BatteryVectors.Information(), null, reason: "status failed");

        BatteryFlow flow = Assert.IsType<BatteryFlow>(BatteryTelemetry.Summarise([
            Battery(BatteryVectors.OnLineDischarging, 10_000, -3_000), unread,
        ]));

        Assert.Null(flow.RateMilliwatts);
        Assert.Null(flow.ChargePercent);
        Assert.Contains("1 of the 2 system batteries", flow.CoverageNote);
    }

    [Fact]
    public void ABatteryWhoseRoleWasNotReadMakesTheTotalUnknown()
    {
        // Without its information block, whether it runs this PC is unknown, so it cannot be left out.
        BatteryReport noInformation = BatteryTelemetry.Decode(2, null,
            BatteryVectors.Status(BatteryVectors.OnLineDischarging, 40, 12_000, -200), reason: "information failed");

        BatteryFlow flow = Assert.IsType<BatteryFlow>(BatteryTelemetry.Summarise([
            Battery(BatteryVectors.OnLineDischarging, 10_000, -3_000), noInformation,
        ]));

        Assert.Null(flow.RateMilliwatts);
        Assert.Null(flow.ChargePercent);
        Assert.NotNull(flow.CoverageNote);
    }

    [Fact]
    public void ABatteryDeviceThatDidNotAnswerMakesTheTotalUnknown()
    {
        BatteryFlow flow = Assert.IsType<BatteryFlow>(BatteryTelemetry.Summarise([
            Battery(BatteryVectors.OnLineDischarging, 10_000, -3_000),
            BatteryTelemetry.Unanswered(2, "The battery device could not be opened."),
        ]));

        Assert.Null(flow.RateMilliwatts);
        Assert.Null(flow.ChargePercent);
        Assert.NotNull(flow.CoverageNote);
    }

    [Fact]
    public void AnEmptySlotIsNoCoverageGap()
    {
        BatteryFlow flow = Assert.IsType<BatteryFlow>(BatteryTelemetry.Summarise([
            Battery(BatteryVectors.OnLineDischarging, 10_000, -3_000),
            BatteryTelemetry.Absent(2, "No battery is in this battery slot."),
        ]));

        Assert.Equal(-3_000, flow.RateMilliwatts);
        Assert.Null(flow.CoverageNote);
    }

    [Fact]
    public void AnUpsIsNoCoverageGap()
    {
        BatteryFlow flow = Assert.IsType<BatteryFlow>(BatteryTelemetry.Summarise([
            Battery(BatteryVectors.OnLineDischarging, 10_000, -3_000),
            BatteryTelemetry.Decode(2, BatteryVectors.Information(capabilities: 0), null),
        ]));

        Assert.Equal(-3_000, flow.RateMilliwatts);
        Assert.Null(flow.CoverageNote);
    }
}

/// <summary>
/// The opt-in sample: two capacity readings and the time between them. Capacity is reported in
/// steps, so this is an average over the interval and a zero change is not a zero flow.
/// </summary>
public class BatterySampleTests
{
    private static BatteryReport At(uint capacity, uint tag = 7)
    {
        BatteryReport b = BatteryTelemetry.Decode(1, BatteryVectors.Information(),
            BatteryVectors.Status(BatteryVectors.OnLineDischarging, capacity, 12_000, -8_000));
        b.Raw.Tag = tag;
        return b;
    }

    [Fact]
    public void AFallInCapacityIsANegativeAverage()
    {
        // 500 mWh lost in a minute is 30 W out of the battery on average.
        BatterySample s = BatteryTelemetry.Compare(At(30_000), At(29_500), TimeSpan.FromSeconds(60));

        Assert.Equal(-30_000, s.AverageNetMilliwatts);
        Assert.Equal(-500, s.ChangeMilliwattHours);
    }

    [Fact]
    public void NoChangeIsNotCalledNoFlow()
    {
        BatterySample s = BatteryTelemetry.Compare(At(30_000), At(30_000), TimeSpan.FromSeconds(30));

        Assert.Equal(0, s.AverageNetMilliwatts);
        Assert.Contains("not evidence", s.Note);
    }

    [Fact]
    public void AChangedTagInvalidatesTheSample()
    {
        BatterySample s = BatteryTelemetry.Compare(At(30_000, tag: 7), At(29_000, tag: 8), TimeSpan.FromSeconds(60));

        Assert.Null(s.AverageNetMilliwatts);
        Assert.Contains("tag", s.Note);
    }

    [Fact]
    public void UnknownCapacityGivesNoAverage()
    {
        BatteryReport unknown = BatteryTelemetry.Decode(1, BatteryVectors.Information(),
            BatteryVectors.Status(0, 0xFFFFFFFF, 12_000, int.MinValue));
        unknown.Raw.Tag = 7;

        BatterySample s = BatteryTelemetry.Compare(At(30_000), unknown, TimeSpan.FromSeconds(60));

        Assert.Null(s.AverageNetMilliwatts);
        Assert.NotNull(s.Note);
    }

    private static string Render(params BatterySample[] samples)
    {
        var output = new StringWriter();
        BatterySampleText.Write(samples, output, (text, _) => text);
        return output.ToString();
    }

    [Fact]
    public void ARenderedZeroChangeIsNoChange_AndSaysTheReportingIsCoarse()
    {
        string text = Render(BatteryTelemetry.Compare(At(30_000), At(30_000), TimeSpan.FromSeconds(30)));

        Assert.Contains("no change in reported capacity over the interval", text);
        Assert.Contains("not evidence that no charge moved", text);
        Assert.Contains(BatteryTelemetry.CoarseReportingNote, text);
        // The first version printed "gaining 0.0W" beside the note saying it was not a zero flow.
        Assert.DoesNotContain("gaining", text);
        Assert.DoesNotContain("losing", text);
    }

    [Fact]
    public void ARenderedChangeIsAnAverage_AndSaysTheReportingIsCoarse()
    {
        string text = Render(BatteryTelemetry.Compare(At(30_000), At(29_500), TimeSpan.FromSeconds(60)));

        Assert.Contains("30000 mWh at the start, 29500 mWh at the end", text);
        Assert.Contains("losing 30.0W net, averaged over the interval", text);
        Assert.Contains(BatteryTelemetry.CoarseReportingNote, text);
        Assert.Contains("not the power arriving through the cable", text);
    }

    [Fact]
    public void NothingRenderedWithoutASample()
    {
        Assert.Equal("", Render());
    }

    private static BatteryReport OnDevice(int index, string path, uint capacity, uint tag = 1)
    {
        BatteryReport b = BatteryTelemetry.Decode(index, BatteryVectors.Information(),
            BatteryVectors.Status(BatteryVectors.OnLineDischarging, capacity, 12_000, -8_000));
        b.Raw.Tag = tag;
        b.DevicePath = path;
        return b;
    }

    [Fact]
    public void ReadingsArePairedByDeviceNotByPosition()
    {
        // Tags are per device, so two batteries can share one. The first version paired by
        // enumeration index, which pairs the wrong batteries when the order changes.
        BatteryReport[] start = [OnDevice(1, @"\\?\battery#a", 30_000), OnDevice(2, @"\\?\battery#b", 10_000)];
        BatteryReport[] end = [OnDevice(1, @"\\?\battery#b", 9_500), OnDevice(2, @"\\?\battery#a", 29_000)];

        List<BatterySample> samples = BatteryTelemetry.Pair(start, end, TimeSpan.FromSeconds(60));

        Assert.Equal(2, samples.Count);
        BatterySample a = samples.Single(s => s.Start!.DevicePath == @"\\?\battery#a");
        BatterySample b = samples.Single(s => s.Start!.DevicePath == @"\\?\battery#b");
        Assert.Equal(@"\\?\battery#a", a.End!.DevicePath);
        Assert.Equal(-1_000, a.ChangeMilliwattHours);
        Assert.Equal(@"\\?\battery#b", b.End!.DevicePath);
        Assert.Equal(-500, b.ChangeMilliwattHours);
    }

    [Fact]
    public void ABatteryThatLeavesIsNotPairedWithTheOneThatTakesItsPlace()
    {
        BatteryReport[] start = [OnDevice(1, @"\\?\battery#a", 30_000), OnDevice(2, @"\\?\battery#b", 10_000)];
        BatteryReport[] end = [OnDevice(1, @"\\?\battery#b", 9_500)];

        List<BatterySample> samples = BatteryTelemetry.Pair(start, end, TimeSpan.FromSeconds(60));

        BatterySample a = samples.Single(s => s.Start!.DevicePath == @"\\?\battery#a");
        Assert.Null(a.End);
        Assert.Null(a.AverageNetMilliwatts);
        Assert.Contains("did not answer", a.Note);
        Assert.Equal(-500, samples.Single(s => s.Start!.DevicePath == @"\\?\battery#b").ChangeMilliwattHours);
    }

    [Fact]
    public void ASameDeviceWithANewTagIsStillNotCompared()
    {
        BatteryReport[] start = [OnDevice(1, @"\\?\battery#a", 30_000, tag: 1)];
        BatteryReport[] end = [OnDevice(1, @"\\?\battery#a", 29_000, tag: 2)];

        BatterySample s = Assert.Single(BatteryTelemetry.Pair(start, end, TimeSpan.FromSeconds(60)));

        Assert.Null(s.AverageNetMilliwatts);
        Assert.Contains("tag", s.Note);
    }
}

/// <summary>
/// The battery's measured charge flow alongside the contract gap. It is the one reading here that
/// can say the machine is running down, and it is still not the power arriving through the cable.
/// </summary>
public class ChargeDiagnosticBatteryFlowTests
{
    private static string Explain(BatteryFlow? flow, int? percent = 30) => Assert.IsType<string>(
        ChargeDiagnostic.Explain(15_000, 100_000, consuming: true, ChargeDiagnostic.Nominal, percent, flow));

    private static BatteryFlow Draining(int? rate) =>
        new(rate, OnExternalPower: true, Discharging: true, Charging: false, ChargePercent: 30);

    [Fact]
    public void AMeasuredDrainOnExternalPowerIsStatedInWatts()
    {
        // The state this was written for: 15W in force, 100W offered, battery falling from 46 to
        // 30 percent while Windows said charging.
        string diagnosis = Explain(Draining(-8_214));

        Assert.Contains("8.2W", diagnosis);
        Assert.Contains("losing charge despite the charger", diagnosis);
        Assert.Contains("Windows' charging indicator is not evidence either way", diagnosis);
    }

    [Fact]
    public void BatteryFlowIsNeverCalledChargerOrCablePower()
    {
        string diagnosis = Explain(Draining(-8_214));

        Assert.DoesNotContain("charger is delivering", diagnosis);
        Assert.DoesNotContain("8.2W from the charger", diagnosis);
        Assert.DoesNotContain("8.2W through the cable", diagnosis);
        Assert.Contains("not the power arriving through the cable", diagnosis);
    }

    [Fact]
    public void AnUnknownRateIsSaidToBeUnknown()
    {
        string diagnosis = Explain(Draining(null));

        Assert.Contains("did not report", diagnosis);
        Assert.DoesNotContain("losing charge despite", diagnosis);
        Assert.DoesNotContain(" 0W", diagnosis);
    }

    [Fact]
    public void AZeroRateIsNotTakenAsAHoldingCharge()
    {
        // Microsoft notes some batteries report only discharging rates, and this machine's WMI
        // charge rate read zero while the battery fell.
        string diagnosis = Explain(new BatteryFlow(0, true, false, true, 30));

        Assert.Contains("rate of zero", diagnosis);
        Assert.Contains("not taken as evidence", diagnosis);
    }

    [Fact]
    public void AMeasuredChargeDoesNotClearTheSupply()
    {
        string diagnosis = Explain(new BatteryFlow(4_000, true, false, true, 30));

        Assert.Contains("4W", diagnosis);
        Assert.Contains("not the power arriving through the cable", diagnosis);
        Assert.Contains("whether the supply can deliver what it advertises", diagnosis);
    }

    [Fact]
    public void TheSupplyIsNeverClearedWhateverTheBatterySays()
    {
        foreach (BatteryFlow? flow in new BatteryFlow?[] { null, Draining(-8_000), Draining(null), new(5_000, true, false, true, 95) })
        foreach (int? percent in new int?[] { null, 30, 97 })
        {
            string diagnosis = Explain(flow, percent);
            Assert.Contains("whether the supply can deliver what it advertises", diagnosis);
            Assert.DoesNotContain("faulty", diagnosis);
        }
    }

    [Fact]
    public void AHighPercentageStaysAPossibilityEvenWithAMeasuredDrain()
    {
        string diagnosis = Explain(Draining(-6_000), percent: 95);

        Assert.Contains("could explain", diagnosis);
        Assert.Contains("does not rule out a fault", diagnosis);
        Assert.DoesNotContain("expected", diagnosis);
    }

    [Fact]
    public void TheWindowsIndicatorIsNotSaidTwice()
    {
        string diagnosis = Explain(Draining(-8_000), percent: 30);

        Assert.DoesNotContain("Windows can report this as charging", diagnosis);
        Assert.Contains("Windows' charging indicator", diagnosis);
    }

    [Fact]
    public void WithoutABatteryDeviceThePercentageWordingIsUnchanged()
    {
        // GetSystemPowerStatus remains the fallback when no battery device answers.
        string diagnosis = Explain(null, percent: 43);

        Assert.Contains("Windows can report this as charging", diagnosis);
        Assert.DoesNotContain("battery measures", diagnosis);
    }

    [Fact]
    public void AMeasuredGainDropsTheFallingBatteryWarning()
    {
        // The first version dropped the warning only for a measured drain, so a measured gain
        // below 90 percent read "net charge of 12W" and then warned that the battery might be falling.
        string diagnosis = Explain(new BatteryFlow(12_000, true, false, true, 30));

        Assert.Contains("net charge of 12W", diagnosis);
        Assert.DoesNotContain("Windows can report this as charging", diagnosis);
    }

    [Fact]
    public void AZeroOrUnknownRateKeepsTheFallingBatteryWarning()
    {
        Assert.Contains("Windows can report this as charging", Explain(new BatteryFlow(0, true, false, true, 30)));
        Assert.Contains("Windows can report this as charging", Explain(Draining(null)));
    }

    [Fact]
    public void ACoverageGapIsStatedInTheDiagnosis()
    {
        string diagnosis = Explain(new BatteryFlow(null, true, true, false, null,
            CoverageNote: "Not every battery could be read."));

        Assert.Contains("Not every battery could be read.", diagnosis);
        Assert.DoesNotContain("net drain", diagnosis);
    }

    [Fact]
    public void NoFlowSentenceWithoutAShortfall()
    {
        Assert.Null(ChargeDiagnostic.Explain(45_000, 65_000, consuming: true, ChargeDiagnostic.Nominal, 30, Draining(-8_000)));
    }
}
