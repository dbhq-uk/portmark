using Portmark.Core.Model;
using Portmark.Core.Ucsi;
using Xunit;

namespace Portmark.Core.Tests;

/// <summary>
/// The CCI indicator bits, checked against values this controller actually returned.
///
/// These were wrong once. The first version placed Busy, Acknowledge, Error and Not Supported two
/// bits low, which made the controller's explicit "Not Supported" answer to GET_CABLE_PROPERTY
/// look like a plain completion, and its "Error" answer to GET_ALTERNATE_MODES look like an empty
/// success. Both readings went into the spike write-up before anyone noticed.
/// </summary>
public class CciTests
{
    [Fact]
    public void NotSupportedIsBit25()
    {
        // GET_CABLE_PROPERTY on the ThinkPad T16 Gen 2 (AMD) returns exactly this, and so does an
        // undefined opcode (0x7F): the controller is saying it does not implement the command.
        var r = new UcsiResult(true, 0x82000000, [], null);

        Assert.True(r.NotSupported);
        Assert.False(r.Errored);
        Assert.Equal(0, r.DataLength);
    }

    [Fact]
    public void ErrorIsBit30()
    {
        // GET_ALTERNATE_MODES with the connector number in the wrong field returns this, and
        // GET_ERROR_STATUS straight afterwards reports "non-existent connector number".
        var r = new UcsiResult(true, 0xC0000000, [], null);

        Assert.True(r.Errored);
        Assert.False(r.NotSupported);
    }

    [Fact]
    public void CompletionAloneSetsNoStatusIndicator()
    {
        var r = new UcsiResult(true, 0x80001000, new byte[16], null);

        Assert.False(r.Errored);
        Assert.False(r.NotSupported);
        Assert.Equal(16, r.DataLength);
    }

    [Fact]
    public void IndicatorBitsMatchTheSpecification()
    {
        // UCSI CCI, bits 25 to 31: Not Supported, Cancel Completed, Reset Completed, Busy,
        // Acknowledge Command, Error, Command Completed.
        Assert.Equal(1u << 25, UcsiProtocol.CciNotSupported);
        Assert.Equal(1u << 28, UcsiProtocol.CciBusy);
        Assert.Equal(1u << 29, UcsiProtocol.CciAcknowledge);
        Assert.Equal(1u << 30, UcsiProtocol.CciError);
        Assert.Equal(1u << 31, UcsiProtocol.CciCommandCompleted);
    }
}

/// <summary>
/// GET_ALTERNATE_MODES is the one command whose connector number does not sit at bit 16. It sits
/// at bit 24, after a three-bit recipient and five reserved bits. Putting it at bit 19 sent this
/// controller a connector number of zero, and it answered, correctly, that connector zero does
/// not exist. That answer was misread as the command being declined.
/// </summary>
public class GetAlternateModesEncodingTests
{
    [Fact]
    public void ConnectorNumberOccupiesBits24To30()
    {
        // This exact CONTROL returned two modes, 0x17EF and 0x8087, from connector 1 live.
        Assert.Equal(0x01000100000CUL, UcsiProtocol.GetAlternateModes(0, 1, 0, 1));
        // Connector 2, offset 1, one mode: returned 0xFF01 live.
        Assert.Equal(0x00010200000CUL, UcsiProtocol.GetAlternateModes(0, 2, 1, 0));
        // Recipient 1 is the attached partner (SOP).
        Assert.Equal(0x00000101000CUL, UcsiProtocol.GetAlternateModes(1, 1, 0, 0));
    }
}

/// <summary>GET_ALTERNATE_MODES payloads captured from the ThinkPad T16 Gen 2 (AMD).</summary>
public class AlternateModesTests
{
    [Fact]
    public void DecodesTwoModesPerResponse()
    {
        // Connector 1, recipient connector, offset 0, two modes requested: Lenovo then Intel.
        byte[] data = Convert.FromHexString("EF1701000000878001000000");

        List<PortAlternateModeReport> modes = AlternateModes.Decode(data);

        Assert.Equal(2, modes.Count);
        Assert.Equal("0x17EF", modes[0].Svid);
        Assert.Equal("0x8087", modes[1].Svid);
        Assert.Equal("Intel Thunderbolt 3", modes[1].Name);
        Assert.Equal("0x00000001", modes[1].ModeId);
        Assert.All(modes, m => Assert.False(m.IsDisplayPort));
    }

    [Fact]
    public void RecognisesDisplayPort()
    {
        // Connector 1, offset 2: the third and last mode. bNumAltModes in GET_CAPABILITY is 3.
        byte[] data = Convert.FromHexString("01FF03000000");

        PortAlternateModeReport dp = Assert.Single(AlternateModes.Decode(data, firstOffset: 2));

        Assert.Equal("0xFF01", dp.Svid);
        Assert.True(dp.IsDisplayPort);
        Assert.Equal(2, dp.Offset);
        Assert.Equal("0x00000003", dp.ModeId);
    }

    [Fact]
    public void EmptyResponseIsNoModes()
    {
        // Recipient SOP with a 65W charger attached: the partner offers nothing, and the
        // controller completes with zero length rather than an error.
        Assert.Empty(AlternateModes.Decode([]));
    }

    [Fact]
    public void PartialTrailingEntryIsNotDecoded()
    {
        byte[] data = Convert.FromHexString("01FF030000008780");
        Assert.Single(AlternateModes.Decode(data));
    }

    [Fact]
    public void ZeroFilledResponseIsNoModes_NotAModeWithSvidZero()
    {
        // An all-zero response is the hardware saying nothing. Decoding it would invent a mode
        // with SVID 0x0000, which is not an assigned SVID, and that invented mode would then pass
        // the "the partner offers a mode" test.
        Assert.Empty(AlternateModes.Decode(new byte[12]));
    }

    [Fact]
    public void RejectedZeroRecordStillConsumesItsOffset()
    {
        // The controller's offsets are what GET_CURRENT_CAM indexes into, so a rejected record
        // must not shift the ones after it.
        byte[] data = Convert.FromHexString("00000000000001FF03000000");

        PortAlternateModeReport dp = Assert.Single(AlternateModes.Decode(data, firstOffset: 4));

        Assert.Equal(5, dp.Offset);
        Assert.True(dp.IsDisplayPort);
    }
}

/// <summary>
/// The walk over GET_ALTERNATE_MODES, driven by a scripted controller. The captured behaviour of
/// the ThinkPad is the first case; the rest are the ways a different controller could answer.
/// </summary>
public class AlternateModeEnumerationTests
{
    private static UcsiResult Page(string hex)
    {
        byte[] payload = Convert.FromHexString(hex);
        return new UcsiResult(true, 0x80000000u | (uint)(payload.Length << 8), payload, null);
    }

    private static readonly UcsiResult Empty = Page("");
    private static readonly UcsiResult NotSupported = new(true, 0x82000000, [], null);
    private static readonly UcsiResult Error = new(true, 0xC0000000, [], null);
    private static readonly UcsiResult Refused = UcsiResult.Fail("GET_ALTERNATE_MODES was refused after 12 attempts");

    private static AlternateModeListReport Walk(params UcsiResult[] pages)
        => AlternateModes.Enumerate(offset => offset < pages.Length ? pages[offset] : Empty);

    [Fact]
    public void WalksTwoModesPerPageUntilAnEmptyPage()
    {
        // Connector 1 on the ThinkPad: two modes at offset 0, one at offset 2, nothing at 3.
        var pages = new UcsiResult[4];
        pages[0] = Page("EF1701000000878001000000");
        pages[2] = Page("01FF03000000");
        pages[3] = Empty;

        AlternateModeListReport list = AlternateModes.Enumerate(offset => pages[offset]);

        Assert.True(list.DataAvailable);
        Assert.True(list.Complete);
        Assert.Equal(["0x17EF", "0x8087", "0xFF01"], list.Modes.Select(m => m.Svid));
        Assert.Equal([0, 1, 2], list.Modes.Select(m => m.Offset));
    }

    [Fact]
    public void AdvancesByTheNumberOfRecordsReturned()
    {
        // A controller that ignores the count field and answers one mode per page must not have
        // its list cut off after the first mode.
        AlternateModeListReport list = Walk(Page("EF1701000000"), Page("878001000000"), Page("01FF03000000"), Empty);

        Assert.True(list.Complete);
        Assert.Equal(3, list.Modes.Count);
    }

    [Fact]
    public void NothingListedIsACompleteEmptyAnswer()
    {
        // Recipient SOP with a charger attached, captured: zero length on the first page.
        AlternateModeListReport list = Walk(Empty);

        Assert.True(list.DataAvailable);
        Assert.True(list.Complete);
        Assert.Empty(list.Modes);
        Assert.Null(list.Reason);
    }

    [Fact]
    public void NotSupportedBeforeAnyModeIsNoData_NotAnEmptyList()
    {
        AlternateModeListReport list = Walk(NotSupported);

        Assert.False(list.DataAvailable);
        Assert.False(list.Complete);
        Assert.Empty(list.Modes);
        Assert.Contains("not supported", list.Reason);
    }

    [Fact]
    public void TransportFailureBeforeAnyModeIsNoData()
    {
        AlternateModeListReport list = Walk(Refused);

        Assert.False(list.DataAvailable);
        Assert.Contains("refused", list.Reason);
    }

    [Fact]
    public void ErrorMidListKeepsThePrefixAndSaysItIsIncomplete()
    {
        var pages = new UcsiResult[3];
        pages[0] = Page("EF1701000000878001000000");
        pages[2] = Error;

        AlternateModeListReport list = AlternateModes.Enumerate(offset => pages[offset]);

        Assert.True(list.DataAvailable);
        Assert.False(list.Complete);
        Assert.Equal(2, list.Modes.Count);
        Assert.Contains("stopped after 2", list.Reason);
    }

    [Fact]
    public void RepeatedPageStopsTheWalkAndIsReportedIncomplete()
    {
        // A controller that ignores the offset would otherwise be walked to the limit and its two
        // modes reported eight times over.
        UcsiResult same = Page("EF1701000000878001000000");
        AlternateModeListReport list = AlternateModes.Enumerate(_ => same);

        Assert.True(list.DataAvailable);
        Assert.False(list.Complete);
        Assert.Equal(2, list.Modes.Count);
        Assert.Contains("same page", list.Reason);
    }

    [Fact]
    public void ZeroFilledPageIsNotAMode()
    {
        AlternateModeListReport list = Walk(Page("000000000000000000000000"), Empty);

        Assert.True(list.DataAvailable);
        Assert.Empty(list.Modes);
    }

    [Fact]
    public void NeverEndingListIsCappedAndReported()
    {
        int calls = 0;
        AlternateModeListReport list = AlternateModes.Enumerate(offset =>
        {
            calls++;
            // Two distinct modes per page, forever: SVID derived from the offset so no page repeats.
            byte a = (byte)(0x10 + offset), b = (byte)(0x11 + offset);
            return Page($"{a:X2}0001000000{b:X2}0001000000");
        });

        Assert.False(list.Complete);
        Assert.Equal(AlternateModes.MaxModes, list.Modes.Count);
        Assert.Equal(AlternateModes.MaxModes / 2, calls);
        Assert.Contains("without the controller ending", list.Reason);
    }
}

/// <summary>
/// The sentence and the named mode, from the lists and the raw current-mode byte. Every case
/// that says "unknown" here was a confident "none" or "active" in the first version.
/// </summary>
public class AlternateModeInterpretationTests
{
    private static AlternateModeListReport ThinkPadPort1() => new()
    {
        DataAvailable = true, Complete = true,
        Modes = AlternateModes.Decode(Convert.FromHexString("EF170100000087800100000001FF03000000")),
    };

    private static AlternateModeListReport Listed(bool complete, params string[] hexModes) => new()
    {
        DataAvailable = true, Complete = complete,
        Reason = complete ? null : "The list stopped after 1 mode(s): the controller returned an error.",
        Modes = AlternateModes.Decode(Convert.FromHexString(string.Concat(hexModes))),
    };

    private static readonly AlternateModeListReport NoData = new()
        { DataAvailable = false, Reason = "The controller reports GET_ALTERNATE_MODES as not supported." };

    private static AlternateModeListReport ThinkPadPort2() => new()
    {
        DataAvailable = true, Complete = true,
        Modes = AlternateModes.Decode(Convert.FromHexString("EF170100000001FF03000000")),
    };

    [Fact]
    public void ChargerAttachedNamesNoCurrentMode()
    {
        // Captured: connector 1, charger attached, partner list empty and complete, CAM byte 0.
        // Index 0 is what this controller also reports for an empty port, so it confirms nothing.
        (string note, PortAlternateModeReport? active, _) =
            AlternateModes.Interpret(ThinkPadPort1(), Listed(complete: true), connected: true, currentCam: 0);

        Assert.Null(active);
        Assert.Contains("index is 0", note);
        Assert.Contains("no mode is confirmed", note);
    }

    [Fact]
    public void DisplayPortAdapterOnPortTwo_IsNamedFromTheControllersIndexWithACaveat()
    {
        // Captured: connector 2 with a USB-C DisplayPort adapter (Billboard 0x343C, which reports
        // DisplayPort Alternate Mode "entered successfully"). Status 00003B402CB1041300: connected,
        // UFP partner, partner flags 0x01 (alternate mode bit clear). GET_CURRENT_CAM = 0x01, which
        // is DisplayPort in this port's list. Partner list: zero length, complete.
        (string note, PortAlternateModeReport? active, _) = AlternateModes.Interpret(
            ThinkPadPort2(), Listed(complete: true), connected: true, currentCam: 1,
            partnerAlternateModeFlag: false);

        Assert.NotNull(active);
        Assert.True(active.IsDisplayPort);
        Assert.Equal(1, active.Offset);
        Assert.Contains("Nothing here corroborates it", note);
    }

    [Fact]
    public void CamFfMeansNoModeInUse()
    {
        (string note, PortAlternateModeReport? active, _) =
            AlternateModes.Interpret(ThinkPadPort1(), Listed(complete: true, "01FF45000C00"), connected: true, currentCam: 0xFF);

        Assert.Null(active);
        Assert.Contains("reports no alternate mode in use", note);
    }

    [Fact]
    public void UnreadableCamIsUnknown_NotNone()
    {
        (string note, PortAlternateModeReport? active, _) =
            AlternateModes.Interpret(ThinkPadPort1(), Listed(complete: true), connected: true, currentCam: null);

        Assert.Null(active);
        Assert.Contains("did not report a current mode", note);
        Assert.DoesNotContain("none is in use", note);
    }

    [Fact]
    public void EmptyPortNamesNoCurrentModeDespiteCamZero()
    {
        // Captured: connector 2, nothing attached, CAM byte 0. The first release said a mode was
        // active here.
        (string note, PortAlternateModeReport? active, _) =
            AlternateModes.Interpret(ThinkPadPort1(), partner: null, connected: false, currentCam: 0);

        Assert.Null(active);
        Assert.Contains("Nothing is attached", note);
    }

    [Fact]
    public void UnreadableAttachmentIsNotReportedAsNothingAttached()
    {
        (string note, PortAlternateModeReport? active, _) =
            AlternateModes.Interpret(ThinkPadPort1(), partner: null, connected: null, currentCam: 0);

        Assert.Null(active);
        Assert.DoesNotContain("Nothing is attached", note);
        Assert.Contains("could not be read", note);
    }

    [Fact]
    public void FailedPartnerEnumerationFallsBackToTheControllersIndex_AndSaysSo()
    {
        (string note, PortAlternateModeReport? active, _) =
            AlternateModes.Interpret(ThinkPadPort1(), NoData, connected: true, currentCam: 2);

        Assert.NotNull(active);
        Assert.True(active.IsDisplayPort);
        Assert.Contains("could not be read", note);
        Assert.Contains("not supported", note);
        Assert.Contains("Nothing here corroborates it", note);
    }

    [Fact]
    public void FailedPartnerEnumerationWithIndexZeroConfirmsNothing()
    {
        (string note, PortAlternateModeReport? active, _) =
            AlternateModes.Interpret(ThinkPadPort1(), NoData, connected: true, currentCam: 0);

        Assert.Null(active);
        Assert.Contains("no mode is confirmed", note);
    }

    [Fact]
    public void FailedPortEnumerationIsSaidPlainly()
    {
        (string note, PortAlternateModeReport? active, _) =
            AlternateModes.Interpret(NoData, null, connected: true, currentCam: 0);

        Assert.Null(active);
        Assert.Contains("could not be listed", note);
    }

    [Fact]
    public void CurrentModeIsNamedWhenTheIndexPointsAtAModeTheDeviceOffers()
    {
        // A DisplayPort partner and the controller's index pointing at the port's DisplayPort mode.
        (string note, PortAlternateModeReport? active, _) =
            AlternateModes.Interpret(ThinkPadPort1(), Listed(complete: true, "01FF45000C00"), connected: true, currentCam: 2);

        Assert.NotNull(active);
        Assert.True(active.IsDisplayPort);
        Assert.Equal(2, active.Offset);
        Assert.Contains("as the current mode", note);
    }

    [Fact]
    public void IndexPointingAtAModeTheDeviceDoesNotOfferIsNotConfirmed()
    {
        // Index 0 is the port's Lenovo mode; the partner offers only DisplayPort.
        (string note, PortAlternateModeReport? active, _) =
            AlternateModes.Interpret(ThinkPadPort1(), Listed(complete: true, "01FF45000C00"), connected: true, currentCam: 0);

        Assert.Null(active);
        Assert.Contains("could not be confirmed", note);
        Assert.Contains("did not list", note);
    }

    [Fact]
    public void IndexOutOfRangeIsNotConfirmed()
    {
        (string note, PortAlternateModeReport? active, _) =
            AlternateModes.Interpret(ThinkPadPort1(), Listed(complete: true, "01FF45000C00"), connected: true, currentCam: 7);

        Assert.Null(active);
        Assert.Contains("does not match", note);
    }

    [Fact]
    public void IncompleteListsAreLabelledIncomplete()
    {
        (string note, _, _) =
            AlternateModes.Interpret(ThinkPadPort1(), Listed(complete: false, "01FF45000C00"), connected: true, currentCam: 2);

        Assert.Contains("list incomplete", note);
    }

    [Fact]
    public void StatusFlagIsCorroborationNotVeto()
    {
        // Index and the device's list agree; the connector status partner flag is clear. This
        // controller leaves that flag clear even with DisplayPort entered (captured with the
        // 0x343C adapter), so a clear flag cannot be allowed to veto the two signals that agree.
        (string note, PortAlternateModeReport? active, _) = AlternateModes.Interpret(
            ThinkPadPort1(), Listed(complete: true, "01FF45000C00"), connected: true, currentCam: 2,
            partnerAlternateModeFlag: false);

        Assert.NotNull(active);
        Assert.True(active.IsDisplayPort);
        Assert.Contains("and the device offers it", note);
    }

    [Fact]
    public void StatusConfirmingAlternateModeAllowsTheName()
    {
        (_, PortAlternateModeReport? active, _) = AlternateModes.Interpret(
            ThinkPadPort1(), Listed(complete: true, "01FF45000C00"), connected: true, currentCam: 2,
            partnerAlternateModeFlag: true);

        Assert.NotNull(active);
        Assert.True(active.IsDisplayPort);
    }
}

/// <summary>Partner flags from GET_CONNECTOR_STATUS, on the captured vectors.</summary>
public class ConnectorPartnerFlagTests
{
    [Fact]
    public void ChargerReportsUsbOnly_AlternateModeClear()
    {
        // Captured for connector 1 with the 65W charger attached. Bits 21-28 are 0x01: USB set,
        // Alternate Mode clear. This is why the current-mode byte of 0 on that port names nothing.
        var report = new ConnectorReport { Index = 1 };
        ConnectorStatus.Apply(Convert.FromHexString("00002B202CB1041301"), report);

        Assert.True(report.Connected);
        Assert.Equal(0x01, report.PartnerFlags);
        Assert.False(report.PartnerAlternateModeFlag);
    }

    [Fact]
    public void EmptyPortHasNoPartnerFlags()
    {
        // Captured for connector 2 with nothing attached: the flags are not reported, so they
        // are null rather than zero.
        var report = new ConnectorReport { Index = 2 };
        ConnectorStatus.Apply(Convert.FromHexString("000000000000000000"), report);

        Assert.False(report.Connected);
        Assert.Null(report.PartnerFlags);
        Assert.Null(report.PartnerAlternateModeFlag);
    }
}

/// <summary>
/// The one deduction portmark makes, on power objects captured from three real chargers on the
/// ThinkPad T16 Gen 2 (AMD), whose controller cannot read a cable at all.
/// </summary>
public class CableInferenceTests
{
    private static PowerReport Supply(string hex) => new()
    {
        DataAvailable = true,
        PartnerSource = PowerDataObject.DecodeAll(Convert.FromHexString(hex)),
    };

    [Fact]
    public void HundredWattSupplyShowsTheCableCarriesFiveAmps()
    {
        // Captured with the 100W charger: 5V/3A, 9V/3A, 15V/3A, 20V/5A. The supply cannot offer
        // the 5A object without having read the cable, which is the read this PC cannot do.
        CableInferenceReport inferred = Assert.IsType<CableInferenceReport>(
            CableInference.FromPower(Supply("2C91110A2CD112002CB11400F4411600")));

        Assert.Equal(5000, inferred.MinimumCurrentRatingMilliamps);
        Assert.Contains("20V at 5A", inferred.Evidence);
    }

    [Fact]
    public void SixtyFiveWattSupplyCountsBecauseItIsAboveThreeAmps()
    {
        // Captured with the 65W charger: its top object is 20V at 3.25A. That is over the 3A an
        // unmarked cable may carry, so the same rule applies. The Lenovo 65W supply has a captive
        // cable, which is exactly the exception the conclusion refuses to rule out.
        CableInferenceReport inferred = Assert.IsType<CableInferenceReport>(
            CableInference.FromPower(Supply("2C91110A2CD112002CB1140045411600")));

        Assert.Equal(3250, inferred.MinimumCurrentRatingMilliamps);
        Assert.Contains("captive", inferred.Conclusion);
    }

    [Fact]
    public void ThirtyWattSupplyShowsNothingAboutTheCable()
    {
        // Captured with the 30W charger: its top current is 3A, which any USB-C cable may carry
        // without declaring anything. There is nothing to deduce, so nothing is said.
        Assert.Null(CableInference.FromPower(Supply("2C9101082CD10200FAC00300C8B00400")));
    }

    [Fact]
    public void NoPowerDataMeansNoDeduction()
    {
        Assert.Null(CableInference.FromPower(new PowerReport { DataAvailable = false }));
        Assert.Null(CableInference.FromPower(new PowerReport { DataAvailable = true }));
    }

    [Fact]
    public void UnrecognisedObjectsAreNotUsedAsEvidence()
    {
        // An object portmark could not decode might hold any current. Reading one as 5A would be
        // inventing the evidence for the conclusion.
        var power = new PowerReport
        {
            DataAvailable = true,
            PartnerSource = [new PowerObjectReport { Kind = "unrecognised", MaxCurrentMilliamps = 5000 }],
        };

        Assert.Null(CableInference.FromPower(power));
    }

    [Fact]
    public void TheDeductionNeverClaimsSpeedAndNeverClaimsTheCableSpoke()
    {
        CableInferenceReport inferred = Assert.IsType<CableInferenceReport>(
            CableInference.FromPower(Supply("2C91110A2CD112002CB11400F4411600")));

        Assert.Contains("says nothing about its data speed", inferred.Conclusion);
        Assert.Contains("word of the supply rather than of the cable", inferred.Conclusion);
    }

    [Fact]
    public void ACableThatSpokeForItselfIsNotSecondGuessed()
    {
        // The deduction is a fallback. When GET_CABLE_PROPERTY answered, the cable's own words
        // stand alone, and PortmarkReader attaches nothing beside them.
        CableReport spoke = CableProperty.Decode(Convert.FromHexString("2C81641A02"));

        Assert.True(spoke.DataAvailable);
        Assert.Null(spoke.Inferred);
    }
}

/// <summary>
/// The charging rate the controller reports, and the gap between the contract and the offer.
/// Both captured on the ThinkPad T16 Gen 2 (AMD) with the battery at 46 percent.
/// </summary>
public class ChargeDiagnosticTests
{
    [Fact]
    public void ChargingStatusIsReadFromTheNinthByte()
    {
        // Connector 1, 100W charger attached, battery at 46 percent. The ninth byte is 0x01.
        var report = new ConnectorReport { Index = 1 };
        ConnectorStatus.Apply(Convert.FromHexString("00002B202CB1041301"), report);

        Assert.Equal(ChargeDiagnostic.Nominal, report.BatteryChargingStatusCode);
        Assert.Equal("nominal charging rate", report.BatteryChargingStatus);
    }

    [Fact]
    public void SupplyingPortReportsNotCharging()
    {
        // Connector 2, supplying power to a DisplayPort adapter. The ninth byte is 0x00.
        var report = new ConnectorReport { Index = 2 };
        ConnectorStatus.Apply(Convert.FromHexString("00003B402CB1041300"), report);

        Assert.Equal(ChargeDiagnostic.NotCharging, report.BatteryChargingStatusCode);
    }

    [Fact]
    public void AShortResponseLeavesTheChargingStatusUnknown()
    {
        // Eight bytes carry no bit 64. Unknown is null, never "not charging".
        var report = new ConnectorReport { Index = 1 };
        ConnectorStatus.Apply(Convert.FromHexString("00002B202CB10413"), report);

        Assert.Null(report.BatteryChargingStatusCode);
        Assert.Null(report.BatteryChargingStatus);
    }

    [Fact]
    public void FifteenWattsFromAHundredWattSupplyIsReported()
    {
        // The state measured on this machine: 5V/3A in force against a supply offering 20V/5A.
        Assert.True(ChargeDiagnostic.IsUnderNegotiated(15_000, 100_000, consuming: true));

        string diagnosis = Assert.IsType<string>(
            ChargeDiagnostic.Explain(15_000, 100_000, consuming: true, ChargeDiagnostic.Nominal));

        Assert.Contains("15W contract", diagnosis);
        Assert.Contains("offers up to 100W", diagnosis);
        Assert.Contains("nominal charging rate", diagnosis);
    }

    [Fact]
    public void AReasonableShareOfTheOfferIsNotFlagged()
    {
        // 45W of an available 65W is a PC taking what it needs, and needs no explanation.
        Assert.False(ChargeDiagnostic.IsUnderNegotiated(45_000, 65_000, consuming: true));
        Assert.Null(ChargeDiagnostic.Explain(45_000, 65_000, consuming: true, ChargeDiagnostic.Nominal));
    }

    [Fact]
    public void ExactlyHalfIsNotFlagged()
    {
        Assert.False(ChargeDiagnostic.IsUnderNegotiated(15_000, 30_000, consuming: true));
        Assert.True(ChargeDiagnostic.IsUnderNegotiated(14_999, 30_000, consuming: true));
    }

    [Fact]
    public void SupplyingPowerIsNeverAShortfall()
    {
        // When this PC is the source, a small contract is the attached device's choice and says
        // nothing about this machine.
        Assert.False(ChargeDiagnostic.IsUnderNegotiated(2_500, 100_000, consuming: false));
        Assert.Null(ChargeDiagnostic.Explain(2_500, 100_000, consuming: false, ChargeDiagnostic.NotCharging));
    }

    [Fact]
    public void MissingNumbersProduceNoDiagnosis()
    {
        Assert.Null(ChargeDiagnostic.Explain(null, 100_000, consuming: true, ChargeDiagnostic.Nominal));
        Assert.Null(ChargeDiagnostic.Explain(15_000, null, consuming: true, ChargeDiagnostic.Nominal));
        Assert.Null(ChargeDiagnostic.Explain(0, 100_000, consuming: true, ChargeDiagnostic.Nominal));
    }

    [Fact]
    public void TheControllerAgreeingIsSaidDifferentlyFromTheControllerDisagreeing()
    {
        string agrees = Assert.IsType<string>(
            ChargeDiagnostic.Explain(15_000, 100_000, consuming: true, ChargeDiagnostic.Slow));
        string disagrees = Assert.IsType<string>(
            ChargeDiagnostic.Explain(15_000, 100_000, consuming: true, ChargeDiagnostic.Nominal));

        Assert.Contains("The controller agrees", agrees);
        Assert.Contains("nonetheless", disagrees);
    }

    [Fact]
    public void NoCauseIsAsserted()
    {
        // With no battery reading, a nearly full battery and a PC that will not draw more cannot
        // be told apart. Neither may be presented as the cause, and nothing may be called faulty.
        string diagnosis = Assert.IsType<string>(
            ChargeDiagnostic.Explain(15_000, 100_000, consuming: true, ChargeDiagnostic.Nominal));

        Assert.Contains("not on its own a fault", diagnosis);
        Assert.DoesNotContain("faulty", diagnosis);
        Assert.DoesNotContain("broken", diagnosis);
    }
}

/// <summary>
/// The battery charge is what separates the two explanations for a small contract. Getting this
/// wrong is not neutral: the reassuring answer is the one that hides a machine running down.
/// </summary>
public class ChargeDiagnosticBatteryTests
{
    private static string Explain(int? batteryPercent) => Assert.IsType<string>(
        ChargeDiagnostic.Explain(15_000, 100_000, consuming: true, ChargeDiagnostic.Nominal, batteryPercent));

    [Fact]
    public void ANearlyFullBatteryExplainsTheSmallContract()
    {
        string diagnosis = Explain(97);

        Assert.Contains("97 percent", diagnosis);
        Assert.Contains("expected", diagnosis);
    }

    [Fact]
    public void AHalfEmptyBatteryRemovesThatExplanation()
    {
        // The state measured on this machine: 43 percent, 15W in force, 100W offered, and the
        // battery falling. Offering "the battery is full" here would be the reassuring wrong
        // answer.
        string diagnosis = Explain(43);

        Assert.Contains("43 percent", diagnosis);
        Assert.Contains("does not explain this", diagnosis);
        Assert.DoesNotContain("expected", diagnosis);
    }

    [Fact]
    public void TheThresholdIsNinetyPercent()
    {
        Assert.Contains("does not explain this", Explain(ChargeDiagnostic.NearlyFullPercent - 1));
        Assert.Contains("little left", Explain(ChargeDiagnostic.NearlyFullPercent));
    }

    [Fact]
    public void NoBatteryReadingKeepsBothExplanationsOpen()
    {
        // A desktop, or a machine Windows will not answer for. Neither explanation is ruled out,
        // so neither is asserted.
        string diagnosis = Explain(null);

        Assert.Contains("already full draws little", diagnosis);
        Assert.DoesNotContain("percent", diagnosis);
    }

    [Fact]
    public void TheWindowsChargingFlagIsCalledOutWhenTheBatteryIsLow()
    {
        // Windows reported this machine as charging while the battery fell from 46 to 43 percent.
        // A user who checks the tray icon will be told the opposite of the truth.
        Assert.Contains("Windows can report this as charging", Explain(43));
    }
}

/// <summary>
/// Whether a named mode is corroborated by anything beyond the controller's index.
///
/// Both vectors come from connector 2 of the same machine, and they are indistinguishable from
/// this end: index 1, no partner mode list, alternate mode flag clear. A DisplayPort adapter had
/// the mode genuinely entered, confirmed by its Billboard descriptor. An iPhone almost certainly
/// did not. Naming the mode without the caveat would have been right once and wrong once.
/// </summary>
public class ActiveAlternateModeConfirmationTests
{
    private static AlternateModeListReport Port2() => new()
    {
        DataAvailable = true, Complete = true,
        Modes = AlternateModes.Decode(Convert.FromHexString("EF170100000001FF03000000")),
    };

    private static AlternateModeListReport Empty() => new() { DataAvailable = true, Complete = true };

    [Fact]
    public void TheControllersIndexAloneIsNotConfirmation()
    {
        (string note, PortAlternateModeReport? active, bool confirmed) = AlternateModes.Interpret(
            Port2(), Empty(), connected: true, currentCam: 1, partnerAlternateModeFlag: false);

        Assert.NotNull(active);
        Assert.True(active.IsDisplayPort);
        Assert.False(confirmed);
        Assert.Contains("Nothing here corroborates it", note);
    }

    [Fact]
    public void ThePartnerListingTheModeIsConfirmation()
    {
        (_, PortAlternateModeReport? active, bool confirmed) = AlternateModes.Interpret(
            Port2(),
            new AlternateModeListReport
            {
                DataAvailable = true, Complete = true,
                Modes = AlternateModes.Decode(Convert.FromHexString("01FF45000C00")),
            },
            connected: true, currentCam: 1, partnerAlternateModeFlag: false);

        Assert.NotNull(active);
        Assert.True(confirmed);
    }

    [Fact]
    public void TheStatusFlagIsConfirmation()
    {
        (_, PortAlternateModeReport? active, bool confirmed) = AlternateModes.Interpret(
            Port2(), Empty(), connected: true, currentCam: 1, partnerAlternateModeFlag: true);

        Assert.NotNull(active);
        Assert.True(confirmed);
    }

    [Fact]
    public void NothingNamedIsNeverConfirmed()
    {
        (_, PortAlternateModeReport? active, bool confirmed) = AlternateModes.Interpret(
            Port2(), Empty(), connected: true, currentCam: 0, partnerAlternateModeFlag: false);

        Assert.Null(active);
        Assert.False(confirmed);
    }
}
