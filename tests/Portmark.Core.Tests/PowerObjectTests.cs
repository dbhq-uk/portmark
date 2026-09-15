using Portmark.Core.Model;
using Portmark.Core.Ucsi;
using Xunit;

namespace Portmark.Core.Tests;

/// <summary>
/// Power data objects and request data objects, one vector per layout.
///
/// Unlike the captured vectors in DecoderTests, most of these are built by hand from the bit
/// layouts in USB Power Delivery R3.2 section 6.4.1 (power data objects) and 6.4.2 (the Request
/// message), with the arithmetic shown beside each one, because the chargers on the test machine
/// only exercise the fixed and PPS layouts. Where a captured vector exists it is used instead.
/// </summary>
internal static class Pd
{
    /// <summary>Words as GET_PDOS returns them: little-endian, four bytes each.</summary>
    public static byte[] Bytes(params uint[] words)
        => words.SelectMany(BitConverter.GetBytes).ToArray();

    public static List<PowerObjectReport> List(params uint[] words)
        => PowerDataObject.DecodeAll(Bytes(words));

    // Fixed supply: B19..10 voltage in 50 mV, B9..0 max current in 10 mA.
    public const uint Fixed5V3A = 0x0001912C;    // 100 << 10 | 300
    public const uint Fixed9V3A = 0x0002D12C;    // 180 << 10 | 300
    public const uint Fixed15V3A = 0x0004B12C;   // 300 << 10 | 300
    public const uint Fixed20V5A = 0x000641F4;   // 400 << 10 | 500
    public const uint Fixed28V5A = 0x0008C1F4;   // 560 << 10 | 500, an EPR fixed object

    // Variable: 10b, B29..20 max voltage and B19..10 min voltage in 50 mV, B9..0 current in 10 mA.
    public const uint Variable5To20V3A = 0x9901912C;   // 400 << 20 | 100 << 10 | 300

    // Battery: 01b, voltages as variable, B9..0 max power in 250 mW.
    public const uint Battery5To20V30W = 0x59019078;   // 400 << 20 | 100 << 10 | 120

    // SPR PPS: 11b 00b, B27 PPS Power Limited, B24..17 max and B15..8 min voltage in 100 mV,
    // B6..0 max current in 50 mA.
    public const uint Pps3V3To21V5A = 0xC1A42164;          // 210 << 17 | 33 << 8 | 100
    public const uint Pps3V3To21V5ALimited = 0xC9A42164;   // the same with B27 set

    // SPR AVS: 11b 10b, B19..10 max current 9-15V and B9..0 max current 15-20V, both in 10 mA.
    public const uint SprAvs3AThen2A25 = 0xE004B0E1;   // 300 << 10 | 225
    public const uint SprAvs3AUpTo15V = 0xE004B000;    // 300 << 10, no 15-20V current

    // EPR AVS: 11b 01b, B25..17 max voltage and B15..8 min voltage in 100 mV, B7..0 PDP in 1 W.
    public const uint EprAvs15To48V140W = 0xD3C0968C;  // 480 << 17 | 150 << 8 | 140

    /// <summary>Augmented, subtype 11b, which R3.2 reserves.</summary>
    public const uint ReservedAugmented = 0xF0DC2164;
}

public class PowerObjectDecodeTests
{
    [Fact]
    public void VariableSupplyDecodesItsRange()
    {
        PowerObjectReport pdo = PowerDataObject.Decode(Pd.Variable5To20V3A);

        Assert.Equal("variable", pdo.Kind);
        Assert.Equal(5000, pdo.MinVoltageMillivolts);
        Assert.Equal(20000, pdo.MaxVoltageMillivolts);
        Assert.Equal(3000, pdo.MaxCurrentMilliamps);
    }

    [Fact]
    public void BatterySupplyDecodesPowerNotCurrent()
    {
        PowerObjectReport pdo = PowerDataObject.Decode(Pd.Battery5To20V30W);

        Assert.Equal("battery", pdo.Kind);
        Assert.Equal(30000, pdo.MaxPowerMilliwatts);
        Assert.Null(pdo.MaxCurrentMilliamps);
    }

    [Fact]
    public void PpsPowerLimitedBitIsExposed()
    {
        Assert.False(PowerDataObject.Decode(Pd.Pps3V3To21V5A).PowerLimited);

        PowerObjectReport limited = PowerDataObject.Decode(Pd.Pps3V3To21V5ALimited);
        Assert.Equal("programmable", limited.Kind);
        Assert.True(limited.PowerLimited);
        Assert.Equal(21000, limited.MaxVoltageMillivolts);
        Assert.Equal(5000, limited.MaxCurrentMilliamps);
        Assert.Contains("power limited", limited.Display);
    }

    [Fact]
    public void PowerLimitedIsNullForObjectsWithoutTheBit()
    {
        Assert.Null(PowerDataObject.Decode(Pd.Fixed5V3A).PowerLimited);
    }

    [Fact]
    public void SprAdjustableSupplyDecodesBothCurrentTiers()
    {
        PowerObjectReport pdo = PowerDataObject.Decode(Pd.SprAvs3AThen2A25);

        Assert.Equal("adjustable", pdo.Kind);
        Assert.False(pdo.ExtendedPowerRange);
        Assert.Equal(9000, pdo.MinVoltageMillivolts);
        Assert.Equal(20000, pdo.MaxVoltageMillivolts);
        Assert.Equal(3000, pdo.MaxCurrentMilliamps);
        Assert.Equal(2250, pdo.MaxCurrent15To20VoltsMilliamps);
        // 15V x 3A and 20V x 2.25A are both 45W.
        Assert.Equal(45000, pdo.MaxPowerMilliwatts);
    }

    [Fact]
    public void SprAdjustableSupplyWithNoUpperTierStopsAtFifteenVolts()
    {
        PowerObjectReport pdo = PowerDataObject.Decode(Pd.SprAvs3AUpTo15V);

        Assert.Equal("adjustable", pdo.Kind);
        Assert.Equal(15000, pdo.MaxVoltageMillivolts);
        Assert.Null(pdo.MaxCurrent15To20VoltsMilliamps);
        Assert.Equal(45000, pdo.MaxPowerMilliwatts);
    }

    [Fact]
    public void EprAdjustableSupplyDecodesItsStatedPdp()
    {
        PowerObjectReport pdo = PowerDataObject.Decode(Pd.EprAvs15To48V140W);

        Assert.Equal("adjustable", pdo.Kind);
        Assert.True(pdo.ExtendedPowerRange);
        Assert.Equal(15000, pdo.MinVoltageMillivolts);
        Assert.Equal(48000, pdo.MaxVoltageMillivolts);
        Assert.Equal(140000, pdo.PdpMilliwatts);
        Assert.Equal(140000, pdo.MaxPowerMilliwatts);
        // The object states no current, and dividing the PDP by a voltage would invent one.
        Assert.Null(pdo.MaxCurrentMilliamps);
    }

    [Fact]
    public void ReservedAugmentedSubtypeStaysUnrecognised()
    {
        Assert.Equal("unrecognised", PowerDataObject.Decode(Pd.ReservedAugmented).Kind);
    }

    [Fact]
    public void EmptySlotsAreSkippedButKeepTheirPositions()
    {
        // 5V/3A, an empty slot, then 9V/3A. The 9V object is at position 3, and a request for
        // position 3 must find it rather than running off the end of a two-item list.
        List<PowerObjectReport> pdos = Pd.List(Pd.Fixed5V3A, 0, Pd.Fixed9V3A);

        Assert.Equal(2, pdos.Count);
        Assert.Equal(1, pdos[0].Position);
        Assert.Equal(3, pdos[1].Position);
    }

    [Fact]
    public void PositionsCanStartPartWayThroughTheList()
    {
        List<PowerObjectReport> pdos = PowerDataObject.DecodeAll(Pd.Bytes(Pd.Pps3V3To21V5A), firstPosition: 5);
        Assert.Equal(5, Assert.Single(pdos).Position);
    }
}

/// <summary>
/// Objects whose fields contradict themselves. R3.2 section 6.4.1 gives every one of these a
/// minimum that is at most its maximum and a non-zero rating; an object that breaks that is not
/// a supply, whatever its type bits say, and must never reach the cable deduction as evidence.
/// </summary>
public class PowerObjectPlausibilityTests
{
    [Theory]
    [InlineData(0x8646412Cu)]   // variable, min 20V above max 5V
    [InlineData(0x8001912Cu)]   // variable, max voltage zero
    [InlineData(0x99019000u)]   // variable, zero current
    [InlineData(0x46464078u)]   // battery, min 20V above max 5V
    [InlineData(0x40019078u)]   // battery, max voltage zero
    [InlineData(0x59019000u)]   // battery, zero power
    [InlineData(0xC064D264u)]   // PPS, min 21V above max 5V, 5A
    [InlineData(0xC0002164u)]   // PPS, max voltage zero, 5A
    [InlineData(0xC1A42100u)]   // PPS, zero current
    [InlineData(0xD12CC88Cu)]   // EPR AVS, min 20V above max 15V
    [InlineData(0xD000968Cu)]   // EPR AVS, max voltage zero
    [InlineData(0xD3C09600u)]   // EPR AVS, zero PDP
    [InlineData(0xE00000E1u)]   // SPR AVS, no 9-15V current
    public void ContradictoryObjectsAreUnrecognised(uint raw)
    {
        PowerObjectReport pdo = PowerDataObject.Decode(raw);

        Assert.Equal("unrecognised", pdo.Kind);
        Assert.Null(pdo.MaxCurrentMilliamps);
        Assert.Null(pdo.MaxPowerMilliwatts);
    }

    [Fact]
    public void AContradictoryFiveAmpObjectNeverTriggersTheCableDeduction()
    {
        // PPS whose current field reads 5A, but whose minimum voltage is above its maximum.
        var power = new PowerReport
        {
            DataAvailable = true,
            PartnerSource = Pd.List(Pd.Fixed5V3A, 0xC064D264),
        };

        Assert.Null(CableInference.FromPower(power));
    }

    [Fact]
    public void AZeroVoltFiveAmpVariableObjectNeverTriggersTheCableDeduction()
    {
        var power = new PowerReport
        {
            DataAvailable = true,
            PartnerSource = Pd.List(Pd.Fixed5V3A, 0x800001F4),   // variable, 0-0V, 5A
        };

        Assert.Null(CableInference.FromPower(power));
    }
}

/// <summary>
/// The Request Data Object, R3.2 section 6.4.2. B31..28 is the object position in all layouts,
/// and B26 Capability Mismatch. The rest depends on the kind of object the position selects.
/// </summary>
public class RequestObjectTests
{
    private static readonly List<PowerObjectReport> HundredWatt =
        Pd.List(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A);

    [Fact]
    public void FixedRequestDecodesOperatingAndMaximumCurrent()
    {
        // Position 4, Capability Mismatch, operating 2.25A, max 3A:
        // 4 << 28 | 1 << 26 | 225 << 10 | 300.
        RequestReport r = Assert.IsType<RequestReport>(PowerDataObject.DecodeRequest(0x4403852C, HundredWatt));

        Assert.Equal(4, r.ObjectPosition);
        Assert.Equal("fixed", r.SelectedKind);
        Assert.True(r.CapabilityMismatch);
        Assert.False(r.GiveBack);
        Assert.Equal(2250, r.OperatingCurrentMilliamps);
        Assert.Equal(3000, r.MaxOperatingCurrentMilliamps);
        Assert.Null(r.MinOperatingCurrentMilliamps);
        Assert.Equal(20000, r.SelectedVoltageMillivolts);
        Assert.Equal(45000, r.NegotiatedPowerMilliwatts);
        Assert.Equal("20V at 2.25A (45W)", r.Display);
    }

    [Fact]
    public void GiveBackTurnsTheLowFieldIntoAMinimum()
    {
        // Position 2, GiveBack, operating 3A, minimum 1A: 2 << 28 | 1 << 27 | 300 << 10 | 100.
        RequestReport r = Assert.IsType<RequestReport>(PowerDataObject.DecodeRequest(0x2804B064, HundredWatt));

        Assert.True(r.GiveBack);
        Assert.False(r.CapabilityMismatch);
        Assert.Equal(1000, r.MinOperatingCurrentMilliamps);
        Assert.Null(r.MaxOperatingCurrentMilliamps);
        Assert.Equal(27000, r.NegotiatedPowerMilliwatts);
    }

    [Fact]
    public void BatteryRequestIsInPowerNotCurrent()
    {
        // Position 3 selects the battery object. Operating 30W, max 45W:
        // 3 << 28 | 120 << 10 | 180, in 250 mW units.
        List<PowerObjectReport> offered = Pd.List(Pd.Fixed5V3A, Pd.Variable5To20V3A, Pd.Battery5To20V30W);
        RequestReport r = Assert.IsType<RequestReport>(PowerDataObject.DecodeRequest(0x3001E0B4, offered));

        Assert.Equal("battery", r.SelectedKind);
        Assert.Equal(30000, r.OperatingPowerMilliwatts);
        Assert.Equal(45000, r.MaxOperatingPowerMilliwatts);
        Assert.Null(r.OperatingCurrentMilliamps);
        Assert.Null(r.MaxOperatingCurrentMilliamps);
        Assert.Null(r.SelectedVoltageMillivolts);
        Assert.Equal(30000, r.NegotiatedPowerMilliwatts);
        Assert.Contains("30W", r.Display);
    }

    [Fact]
    public void VariableRequestDoesNotInventAVoltageOrAPower()
    {
        // Position 2, operating 1.5A, max 3A: 2 << 28 | 150 << 10 | 300. The request names a
        // current but not where in the 5-20V range the supply sits, so the power is unknown.
        List<PowerObjectReport> offered = Pd.List(Pd.Fixed5V3A, Pd.Variable5To20V3A);
        RequestReport r = Assert.IsType<RequestReport>(PowerDataObject.DecodeRequest(0x2002592C, offered));

        Assert.Equal("variable", r.SelectedKind);
        Assert.Equal(1500, r.OperatingCurrentMilliamps);
        Assert.Equal(3000, r.MaxOperatingCurrentMilliamps);
        Assert.Null(r.SelectedVoltageMillivolts);
        Assert.Null(r.NegotiatedPowerMilliwatts);
        Assert.Contains("1.5A", r.Display);
        Assert.DoesNotContain("W)", r.Display);
    }

    [Fact]
    public void ProgrammableRequestCarriesItsOwnVoltage()
    {
        // Position 5 selects PPS. Output 9.5V in 20 mV units, operating 2A in 50 mA units:
        // 5 << 28 | 475 << 9 | 40.
        List<PowerObjectReport> offered = Pd.List(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A,
                                                  Pd.Pps3V3To21V5A);
        RequestReport r = Assert.IsType<RequestReport>(PowerDataObject.DecodeRequest(0x5003B628, offered));

        Assert.Equal(5, r.ObjectPosition);
        Assert.Equal("programmable", r.SelectedKind);
        Assert.Equal(9500, r.OutputVoltageMillivolts);
        Assert.Equal(2000, r.OperatingCurrentMilliamps);
        Assert.Null(r.MaxOperatingCurrentMilliamps);
        Assert.Null(r.GiveBack);   // reserved in this layout
        Assert.Equal(19000, r.NegotiatedPowerMilliwatts);
        Assert.Equal("PPS 9.5V at 2A (19W)", r.Display);
    }

    [Fact]
    public void AdjustableRequestUsesTwentyFiveMillivoltSteps()
    {
        // Position 6 selects SPR AVS. Output 15V in 25 mV units, operating 3A in 50 mA units:
        // 6 << 28 | 600 << 9 | 60.
        List<PowerObjectReport> offered = Pd.List(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A,
                                                  Pd.Pps3V3To21V5A, Pd.SprAvs3AThen2A25);
        RequestReport r = Assert.IsType<RequestReport>(PowerDataObject.DecodeRequest(0x6004B03C, offered));

        Assert.Equal("adjustable", r.SelectedKind);
        Assert.Equal(15000, r.OutputVoltageMillivolts);
        Assert.Equal(3000, r.OperatingCurrentMilliamps);
        Assert.Equal(45000, r.NegotiatedPowerMilliwatts);
        Assert.Equal("AVS 15V at 3A (45W)", r.Display);
    }

    [Fact]
    public void ObjectPositionIsFourBits()
    {
        // EPR position 8, with positions 5-7 empty as R3.2 pads them. A three-bit field reads
        // this as position 0. Operating and max 5A: 8 << 28 | 500 << 10 | 500.
        List<PowerObjectReport> offered = Pd.List(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A,
                                                  0, 0, 0, Pd.Fixed28V5A);
        RequestReport r = Assert.IsType<RequestReport>(PowerDataObject.DecodeRequest(0x8007D1F4, offered));

        Assert.Equal(8, r.ObjectPosition);
        Assert.Equal(28000, r.SelectedVoltageMillivolts);
        Assert.Equal(140000, r.NegotiatedPowerMilliwatts);
    }

    [Fact]
    public void AnEmptySlotDoesNotShiftTheSelection()
    {
        // Position 3 with an empty slot at 2. Operating and max 3A: 3 << 28 | 300 << 10 | 300.
        List<PowerObjectReport> offered = Pd.List(Pd.Fixed5V3A, 0, Pd.Fixed9V3A);
        RequestReport r = Assert.IsType<RequestReport>(PowerDataObject.DecodeRequest(0x3004B12C, offered));

        Assert.Equal(9000, r.SelectedVoltageMillivolts);
        Assert.Equal(27000, r.NegotiatedPowerMilliwatts);
    }

    [Fact]
    public void AnUnreadPositionLeavesEveryLayoutDependentFieldUnknown()
    {
        // Without the selected object, which layout the low 28 bits follow is unknown.
        RequestReport r = Assert.IsType<RequestReport>(PowerDataObject.DecodeRequest(0x7004B12C, HundredWatt));

        Assert.Equal(7, r.ObjectPosition);
        Assert.Null(r.SelectedKind);
        Assert.Null(r.OperatingCurrentMilliamps);
        Assert.Null(r.MaxOperatingCurrentMilliamps);
        Assert.Null(r.NegotiatedPowerMilliwatts);
        // The low bits read as 3A under the fixed layout, which this position may not be.
        Assert.DoesNotContain("3A", r.Display);
    }

    [Fact]
    public void AnUnrecognisedSelectedObjectIsNotDecodedAsFixed()
    {
        List<PowerObjectReport> offered = Pd.List(Pd.Fixed5V3A, Pd.ReservedAugmented);
        RequestReport r = Assert.IsType<RequestReport>(PowerDataObject.DecodeRequest(0x2004B12C, offered));

        Assert.Null(r.SelectedKind);
        Assert.Null(r.OperatingCurrentMilliamps);
        Assert.Null(r.NegotiatedPowerMilliwatts);
    }

    [Fact]
    public void TheRequestIsResolvedAgainstWhicheverSideIsTheSource()
    {
        var power = new PowerReport
        {
            PartnerSource = Pd.List(Pd.Fixed20V5A),
            LocalSource = Pd.List(Pd.Fixed5V3A),
        };

        Assert.Same(power.LocalSource, PortmarkReader.SourceSideObjects(power, "supplying"));
        Assert.Same(power.PartnerSource, PortmarkReader.SourceSideObjects(power, "consuming"));
        Assert.Empty(PortmarkReader.SourceSideObjects(power, null));
    }
}

/// <summary>
/// GET_PDOS returns at most four objects per call (NumberOfPdos is two bits), and a USB PD SPR
/// source can advertise seven, so the rest need a second read at offset 4.
/// </summary>
public class SourceListPagingTests
{
    private static UcsiResult Page(params uint[] words)
    {
        byte[] bytes = Pd.Bytes(words);
        return new UcsiResult(true, 0x80000000u | ((uint)bytes.Length << 8), bytes, null);
    }

    [Fact]
    public void AFullFirstPageIsFollowedByOneReadAtOffsetFour()
    {
        var asked = new List<(byte Offset, byte NumberMinusOne)>();
        PowerDataObject.SourceList list = PowerDataObject.ReadSourceList((offset, n) =>
        {
            asked.Add((offset, n));
            return offset == 0
                ? Page(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A)
                : Page(Pd.Pps3V3To21V5A, Pd.SprAvs3AThen2A25, Pd.Variable5To20V3A);
        });

        Assert.Equal(new (byte, byte)[] { (0, 3), (4, 2) }, asked);
        Assert.Equal(7, list.Objects.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, list.Objects.Select(p => p.Position ?? 0));
        Assert.Equal(28, list.Bytes.Length);
    }

    [Fact]
    public void AShortFirstPageEndsTheList()
    {
        int calls = 0;
        PowerDataObject.SourceList list = PowerDataObject.ReadSourceList((_, _) =>
        {
            calls++;
            return Page(Pd.Fixed5V3A, Pd.Fixed9V3A);
        });

        Assert.Equal(1, calls);
        Assert.Equal(2, list.Objects.Count);
    }

    [Theory]
    [InlineData(0xC0000000u)]   // Error
    [InlineData(0x82000000u)]   // Not Supported
    [InlineData(0x80000000u)]   // completed, zero length
    public void ASecondPageThatIsRefusedOrEmptyKeepsTheFirst(uint cci)
    {
        PowerDataObject.SourceList list = PowerDataObject.ReadSourceList((offset, _) => offset == 0
            ? Page(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A)
            : new UcsiResult(true, cci, [], null));

        Assert.Equal(4, list.Objects.Count);
        Assert.Equal(16, list.Bytes.Length);
    }

    [Fact]
    public void AnErrorFlaggedPageIsNotDecodedEvenIfItCarriesBytes()
    {
        PowerDataObject.SourceList list = PowerDataObject.ReadSourceList((offset, _) => offset == 0
            ? Page(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A)
            : new UcsiResult(true, 0xC0000C00, Pd.Bytes(Pd.Pps3V3To21V5A, 0, 0), null));

        Assert.Equal(4, list.Objects.Count);
    }

    [Fact]
    public void AnAllZeroSecondPageAddsNothing()
    {
        PowerDataObject.SourceList list = PowerDataObject.ReadSourceList((offset, _) => offset == 0
            ? Page(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A)
            : Page(0, 0, 0));

        // Objects.Count alone is 4 whether or not the page was taken, because zero words never
        // decode. The bytes show the difference: an all-zero answer is "nothing to report"
        // (UcsiResult.NoPayload), so the read stops there and the page is not appended.
        Assert.Equal(4, list.Objects.Count);
        Assert.Equal(16, list.Bytes.Length);
        Assert.Equal(Pd.Bytes(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A), list.Bytes);
    }

    [Fact]
    public void AFailedFirstReadReadsNothingFurther()
    {
        int calls = 0;
        PowerDataObject.SourceList list = PowerDataObject.ReadSourceList((_, _) =>
        {
            calls++;
            return UcsiResult.Fail("refused");
        });

        Assert.Equal(1, calls);
        Assert.Empty(list.Objects);
        Assert.Empty(list.Bytes);
    }

    [Fact]
    public void AControllerThatIgnoresTheOffsetDoesNotDuplicateObjects()
    {
        // R3.2 section 6.4.1 has no two objects alike in one list, so a second page that repeats
        // the start of the first is the controller ignoring the offset, not three more offers.
        PowerDataObject.SourceList list = PowerDataObject.ReadSourceList((_, n) => n == 3
            ? Page(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A)
            : Page(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A));

        Assert.Equal(4, list.Objects.Count);
    }

    [Fact]
    public void SourcePdoRequestsNeverSetTheCapabilityTypeField()
    {
        // Bits 35-36, Source Capabilities Type. Zero is the current source capabilities, which
        // every UCSI version answers; on this UCSI 1.0 controller type 3 returned Error.
        ulong control = UcsiProtocol.GetPdos(1, partner: true, 4, 2, source: true);

        Assert.Equal(0x0000000604810010UL, control);
        Assert.Equal(0UL, (control >> 35) & 0x3);
    }
}

public class OfferCeilingTests
{
    [Fact]
    public void ProgrammableVoltageTimesCurrentDoesNotRaiseTheCeiling()
    {
        // The 100W charger's fixed objects plus a 3.3-21V 5A PPS object. 21V x 5A is 105W,
        // which the supply never offered.
        List<PowerObjectReport> offered = Pd.List(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A,
                                                  Pd.Pps3V3To21V5A);

        Assert.Equal(100000, PowerDataObject.OfferCeilingMilliwatts(offered));
    }

    [Fact]
    public void EprAdjustablePdpCounts()
    {
        List<PowerObjectReport> offered = Pd.List(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A,
                                                  0, 0, 0, Pd.EprAvs15To48V140W);

        Assert.Equal(140000, PowerDataObject.OfferCeilingMilliwatts(offered));
    }

    [Fact]
    public void NoFixedObjectMeansNoCeiling()
    {
        Assert.Null(PowerDataObject.OfferCeilingMilliwatts(Pd.List(Pd.Pps3V3To21V5A)));
        Assert.Null(PowerDataObject.OfferCeilingMilliwatts([]));
    }

    [Fact]
    public void TheCapturedHundredWattChargerStillReadsHundredWatts()
    {
        List<PowerObjectReport> offered =
            PowerDataObject.DecodeAll(Convert.FromHexString("2C91110A2CD112002CB11400F4411600"));

        Assert.Equal(100000, PowerDataObject.OfferCeilingMilliwatts(offered));
    }
}

public class PowerSummaryTests
{
    private static ConnectorReport Port(string direction, PowerReport power) => new()
    {
        Index = 2,
        Connected = true,
        PowerDirection = direction,
        PartnerType = "Upstream facing port",
        PowerOperationMode = "USB Power Delivery",
        Power = power,
        Cable = new CableReport { DataAvailable = false },
    };

    [Fact]
    public void SupplyingPortDescribesWhatThisPcOffersAndSupplies()
    {
        List<PowerObjectReport> local = Pd.List(Pd.Fixed5V3A);
        var power = new PowerReport
        {
            DataAvailable = true,
            LocalSource = local,
            Negotiated = PowerDataObject.DecodeRequest(0x1304B12C, local),
        };

        string summary = PortmarkReader.Summarise(Port("supplying", power));

        Assert.DoesNotContain("drawing", summary);
        Assert.DoesNotContain("supply offers", summary);
        Assert.Contains("this PC offers up to 15W, with a contract of 5V at 3A (15W)", summary);
        // The RDO is a negotiated request, not a measurement of what is flowing.
        Assert.DoesNotContain("supplying 5V", summary);
    }

    [Fact]
    public void SupplyingPortNeverCallsThePartnerTheSupply()
    {
        // The attached device lists source objects of its own, but this PC is the source.
        var power = new PowerReport
        {
            DataAvailable = true,
            PartnerSource = Pd.List(Pd.Fixed5V3A),
            MaxAvailableMilliwatts = 15000,
        };

        string summary = PortmarkReader.Summarise(Port("supplying", power));

        Assert.DoesNotContain("drawing", summary);
        Assert.DoesNotContain("supply offers", summary);
    }

    [Fact]
    public void ConsumingPortStillDescribesTheSupply()
    {
        List<PowerObjectReport> partner = Pd.List(Pd.Fixed5V3A, Pd.Fixed9V3A, Pd.Fixed15V3A, Pd.Fixed20V5A);
        var power = new PowerReport
        {
            DataAvailable = true,
            PartnerSource = partner,
            MaxAvailableMilliwatts = PowerDataObject.OfferCeilingMilliwatts(partner),
            Negotiated = PowerDataObject.DecodeRequest(0x1304B12C, partner),
        };

        string summary = PortmarkReader.Summarise(Port("consuming", power));

        Assert.Contains("supply offers up to 100W, with a contract of 5V at 3A (15W)", summary);
        Assert.Contains("drawing power", summary);
        Assert.DoesNotContain("drawing 5V", summary);
    }
}
