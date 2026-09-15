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
                                     uint designed = 52_500, uint fullCharged = 47_880, uint cycles = 0)
    {
        var bytes = new byte[BatteryTelemetry.InformationLength];
        BitConverter.GetBytes(capabilities).CopyTo(bytes, 0);
        bytes[4] = 1;   // Technology: rechargeable
        for (int i = 0; i < 4 && i < chemistry.Length; i++) bytes[8 + i] = (byte)chemistry[i];
        BitConverter.GetBytes(designed).CopyTo(bytes, 12);
        BitConverter.GetBytes(fullCharged).CopyTo(bytes, 16);
        BitConverter.GetBytes(cycles).CopyTo(bytes, 28);
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
        // The driver returned 36 bytes for BatteryInformation, four more than the documented
        // structure, and a 32-byte buffer was refused with ERROR_INSUFFICIENT_BUFFER. Only the
        // documented 32 are decoded; the trailing 0x78 is kept in the raw hex and not guessed at.
        // The design capacity has not been checked against the battery's label.
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
        Assert.Null(b.CycleCount);

        BatteryPowerStateReport state = Assert.IsType<BatteryPowerStateReport>(b.PowerState);
        Assert.True(state.OnExternalPower);
        Assert.True(state.Charging);
        Assert.False(state.Discharging);
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

    [Fact]
    public void EveryNoteSaysTheReportingIsCoarse()
    {
        BatterySample s = BatteryTelemetry.Compare(At(30_000), At(29_500), TimeSpan.FromSeconds(60));

        Assert.Contains("coarse", BatteryTelemetry.CoarseReportingNote);
        Assert.Contains("steps", BatteryTelemetry.CoarseReportingNote);
        Assert.NotNull(s);
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
    public void NoFlowSentenceWithoutAShortfall()
    {
        Assert.Null(ChargeDiagnostic.Explain(45_000, 65_000, consuming: true, ChargeDiagnostic.Nominal, 30, Draining(-8_000)));
    }
}
