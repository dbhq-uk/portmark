using Portmark.Core.Model;
using Portmark.Core.Ucsi;
using Xunit;

namespace Portmark.Core.Tests;

/// <summary>
/// GET_PD_MESSAGE, per UCSI 1.2 Table 4-50 and UCSI 2.0 Table 4-51: Command 0-7, Data Length 8-15,
/// Connector Number 16-22, Recipient 23-25, Message Offset 26-33, Number of Bytes 34-41, Response
/// Message Type 42-47. Linux's UCSI_GET_PD_MESSAGE_RECIPIENT, _OFFSET, _BYTES and _TYPE shift by 23,
/// 26, 34 and 42. The expected CONTROL values were added up by hand from those offsets, not taken
/// from this code or from any captured traffic: this machine's controller does not offer the
/// command, so nothing here has been on the wire.
/// </summary>
public class GetPdMessageEncodingTests
{
    [Fact]
    public void CommandCodeIs0x15()
    {
        // UCSI Table A-1. The table's text extraction shifts the values two rows, so this was
        // cross-checked against Linux's UCSI_GET_PD_MESSAGE 0x15 and the position after
        // SET_POWER_LEVEL, 0x14, the last command in Microsoft's UCSI 1.1 era header.
        Assert.Equal(0x15, UcsiProtocol.CmdGetPdMessage);
        Assert.Equal("GET_PD_MESSAGE", UcsiProtocol.CommandName(UcsiProtocol.CmdGetPdMessage));
    }

    [Fact]
    public void FieldsSitAtTheOffsetsTheTableGives()
    {
        ulong control = UcsiProtocol.GetPdMessage(connector: 2, recipient: UcsiProtocol.PdMessageRecipientSopPrime,
                                                  offset: 16, numberOfBytes: 12,
                                                  responseMessageType: UcsiProtocol.PdMessageDiscoverIdentity);

        Assert.Equal(0x15UL, control & 0xFF);
        Assert.Equal(0UL, (control >> 8) & 0xFF);        // Data Length: shall be 0
        Assert.Equal(2UL, (control >> 16) & 0x7F);
        Assert.Equal(2UL, (control >> 23) & 0x07);       // SOP'
        Assert.Equal(16UL, (control >> 26) & 0xFF);
        Assert.Equal(12UL, (control >> 34) & 0xFF);
        Assert.Equal(4UL, (control >> 42) & 0x3F);       // Discover Identity response
        Assert.Equal(0UL, control >> 48);                // reserved

        // 0x15 + (2 << 16) + (2 << 23) + (16 << 26) + (12 << 34) + (4 << 42)
        // = 0x15 + 0x20000 + 0x1000000 + 0x40000000 + 0x3000000000 + 0x100000000000
        Assert.Equal(0x103041020015UL, control);
    }

    [Fact]
    public void FirstPageForThePartner()
    {
        // 0x15 + (1 << 16) + (1 << 23) + (0 << 26) + (16 << 34) + (4 << 42)
        Assert.Equal(0x104000810015UL,
            UcsiProtocol.GetPdMessage(1, UcsiProtocol.PdMessageRecipientSop, 0, 16, UcsiProtocol.PdMessageDiscoverIdentity));
    }

    [Fact]
    public void RecipientAndTypeValuesMatchTheTable()
    {
        // Recipient: 0 connector, 1 SOP, 2 SOP', 3 SOP''. Response Message Type 4: Discover
        // Identity response (ACK, NAK or BUSY).
        Assert.Equal(1, UcsiProtocol.PdMessageRecipientSop);
        Assert.Equal(2, UcsiProtocol.PdMessageRecipientSopPrime);
        Assert.Equal(4, UcsiProtocol.PdMessageDiscoverIdentity);
    }

    [Fact]
    public void FieldsDoNotSpillIntoTheirNeighbours()
    {
        ulong control = UcsiProtocol.GetPdMessage(0xFF, 0xFF, 0, 0, 0xFF);

        Assert.Equal(0x7FUL, (control >> 16) & 0x7F);
        Assert.Equal(0x07UL, (control >> 23) & 0x07);
        Assert.Equal(0UL, (control >> 26) & 0xFF);
        Assert.Equal(0UL, (control >> 34) & 0xFF);
        Assert.Equal(0x3FUL, (control >> 42) & 0x3F);
        Assert.Equal(0UL, control >> 48);
    }
}

/// <summary>
/// Whether GET_PD_MESSAGE may be sent at all. It makes the controller send Discover Identity on
/// the wire, so it is only sent where the controller has said it supports it: UCSI 1.2 or later,
/// the GET_PD_MESSAGE bit (bmOptionalFeatures bit 8, UCSI 1.2 Table 4-54, Linux
/// UCSI_CAP_GET_PD_MESSAGE) set, and something attached.
/// </summary>
public class PdMessageGateTests
{
    /// <summary>Real GET_CAPABILITY response from the test machine: bmOptionalFeatures 0x000094.</summary>
    private static readonly byte[] Captured = Convert.FromHexString("46400000029400000300020100020001");

    private static PpmFeatureReport Offered(bool bit) => new() { GetPdMessageSupported = bit };

    [Fact]
    public void ThisMachineIsNotAsked()
    {
        // UCSI 1.0, bit 8 clear, a charger attached. The reason must say it is this PC's
        // controller, not the attached device, that rules the question out.
        string? why = PdMessage.WhyNotAsk(0x0100, Capability.Decode(Captured), connected: true);

        Assert.NotNull(why);
        Assert.Contains("does not offer PD messages", why);
        Assert.Contains("cannot be asked to identify themselves", why);
        Assert.Contains("UCSI 1.0", why);
    }

    [Fact]
    public void AControllerThatOffersItIsAsked()
    {
        Assert.Null(PdMessage.WhyNotAsk(0x0120, Offered(true), connected: true));
        Assert.Null(PdMessage.WhyNotAsk(0x0200, Offered(true), connected: true));
        Assert.Null(PdMessage.WhyNotAsk(0x0210, Offered(true), connected: true));
    }

    [Fact]
    public void TheFeatureBitAloneIsNotEnough()
    {
        // Bit 8 is not defined before UCSI 1.2: Microsoft's UCSI 1.1 era structure stops at
        // bit 7. A set bit on an older controller is not an offer anyone defined.
        string? why = PdMessage.WhyNotAsk(0x0110, Offered(true), connected: true);

        Assert.NotNull(why);
        Assert.Contains("1.2", why);
    }

    [Fact]
    public void TheVersionAloneIsNotEnough()
    {
        string? why = PdMessage.WhyNotAsk(0x0200, Offered(false), connected: true);

        Assert.NotNull(why);
        Assert.Contains("does not offer PD messages", why);
    }

    [Fact]
    public void NothingAttachedIsNotAsked()
    {
        string? why = PdMessage.WhyNotAsk(0x0200, Offered(true), connected: false);

        Assert.NotNull(why);
        Assert.Contains("Nothing is attached", why);
    }

    [Fact]
    public void UnknownsAreNotTreatedAsPermission()
    {
        Assert.Contains("could not be read", PdMessage.WhyNotAsk(0x0200, Offered(true), connected: null));
        Assert.Contains("could not be read", PdMessage.WhyNotAsk(0x0200, null, connected: true));
        Assert.Contains("could not be read", PdMessage.WhyNotAsk(null, Offered(true), connected: true));
    }

    [Fact]
    public void FeatureBitEightDecodes()
    {
        Assert.False(Capability.Decode(Captured)!.GetPdMessageSupported);

        // The captured response with byte 6, bmOptionalFeatures bits 15-8, changed to 0x01.
        byte[] offered = Convert.FromHexString("46400000029401000300020100020001");
        PpmFeatureReport features = Capability.Decode(offered)!;

        Assert.True(features.GetPdMessageSupported);
        Assert.Equal("0x000194", features.OptionalFeaturesHex);
        Assert.False(features.CableDetailsAvailable);
    }
}

/// <summary>
/// Reading one message through a 16-byte MESSAGE IN. MAX_DATA_LENGTH is 0x10 in UCSI 1.2 and 2.0
/// (Table A-2), and this transport copies at most 16 bytes of MESSAGE IN. For a Structured VDM the
/// offset has to be a multiple of four below four times the object count, and the controller's
/// Data Length being below the Number of Bytes asked for means the message ended.
/// </summary>
public class PdMessagePagingTests
{
    private static UcsiResult Page(byte[] payload)
        => new(true, 0x80000000u | ((uint)payload.Length << 8), payload, null);

    private static readonly UcsiResult Error = new(true, 0xC0000000, [], null);
    private static readonly UcsiResult NotSupported = new(true, 0x82000000, [], null);

    private static byte[] Message(int length) => Enumerable.Range(1, length).Select(i => (byte)i).ToArray();

    private static Func<byte, byte, UcsiResult> Serve(byte[] message, List<(byte Offset, byte Count)> calls)
        => (offset, count) =>
        {
            calls.Add((offset, count));
            int available = Math.Max(0, Math.Min(count, message.Length - offset));
            return Page(message.AsSpan(Math.Min(offset, message.Length), available).ToArray());
        };

    [Fact]
    public void ReadsSixteenBytesAtATimeAndAdvancesTheOffset()
    {
        var calls = new List<(byte, byte)>();
        byte[] message = Message(28);

        PdMessageTransfer t = PdMessage.Read(Serve(message, calls), maxBytes: 28);

        Assert.Equal([(0, 16), (16, 12)], calls);
        Assert.Equal(message, t.Bytes);
        Assert.False(t.Failed);
    }

    [Fact]
    public void AShortPageEndsTheMessage()
    {
        var calls = new List<(byte, byte)>();

        PdMessageTransfer t = PdMessage.Read(Serve(Message(20), calls), maxBytes: 28);

        Assert.Equal(2, calls.Count);
        Assert.Equal(20, t.Bytes.Length);
    }

    [Fact]
    public void AShortFirstPageIsTheWholeMessage()
    {
        // A NAK is the VDM header alone: four bytes, and no reason to ask for more.
        var calls = new List<(byte, byte)>();

        PdMessageTransfer t = PdMessage.Read(Serve(Message(4), calls), maxBytes: 28);

        Assert.Single(calls);
        Assert.Equal(4, t.Bytes.Length);
    }

    [Fact]
    public void AnEmptyPageEndsTheMessage()
    {
        var calls = new List<(byte, byte)>();

        PdMessageTransfer t = PdMessage.Read(Serve(Message(16), calls), maxBytes: 28);

        Assert.Equal(2, calls.Count);
        Assert.Equal(16, t.Bytes.Length);
        Assert.False(t.Failed);
    }

    [Fact]
    public void AnErrorAfterSomeBytesKeepsThemAndSaysWhy()
    {
        int n = 0;
        PdMessageTransfer t = PdMessage.Read((_, _) => n++ == 0 ? Page(Message(16)) : Error, maxBytes: 28);

        Assert.Equal(16, t.Bytes.Length);
        Assert.False(t.Failed);
        Assert.Contains("error", t.StoppedBecause);
    }

    [Fact]
    public void AnErrorBeforeAnyBytesIsAFailure()
    {
        PdMessageTransfer t = PdMessage.Read((_, _) => Error, maxBytes: 28);

        Assert.True(t.Failed);
        Assert.Empty(t.Bytes);
        Assert.Contains("error", t.StoppedBecause);
    }

    [Fact]
    public void NotSupportedIsAFailureAndSaysSo()
    {
        PdMessageTransfer t = PdMessage.Read((_, _) => NotSupported, maxBytes: 28);

        Assert.True(t.Failed);
        Assert.Contains("not supported", t.StoppedBecause);
    }

    [Fact]
    public void ARefusedRequestIsAFailure()
    {
        PdMessageTransfer t = PdMessage.Read((_, _) => UcsiResult.Fail("the interface went away"), maxBytes: 28);

        Assert.True(t.Failed);
        Assert.Contains("the interface went away", t.StoppedBecause);
    }

    [Fact]
    public void AKnownLengthSavesTheControllerARequest()
    {
        // Every request is a round trip to a controller that has been wedged by rapid commands, so
        // once the ID Header says how long the message is, nothing further is asked.
        var calls = new List<(byte, byte)>();

        PdMessageTransfer t = PdMessage.Read(Serve(Message(28), calls), maxBytes: 28, expectedLength: _ => 16);

        Assert.Single(calls);
        Assert.Equal(16, t.Bytes.Length);
    }

    [Fact]
    public void AKnownLengthLimitsTheLastPage()
    {
        var calls = new List<(byte, byte)>();

        PdMessage.Read(Serve(Message(28), calls), maxBytes: 28, expectedLength: _ => 20);

        Assert.Equal([(0, 16), (16, 4)], calls);
    }

    [Fact]
    public void NeverAsksBeyondTheLongestMessage()
    {
        var calls = new List<(byte, byte)>();

        PdMessageTransfer t = PdMessage.Read((o, c) => { calls.Add((o, c)); return Page(Message(c)); }, maxBytes: 28);

        Assert.Equal(28, t.Bytes.Length);
        Assert.Equal([(0, 16), (16, 12)], calls);
    }
}

/// <summary>
/// Discover Identity responses, decoded per USB PD R3.2 V1.2 section 6.4.12.3 (numbered 6.4.4.3.1 in
/// earlier releases). No response of this kind has been captured: this machine's controller does
/// not offer GET_PD_MESSAGE. Every vector is assembled by hand from the bit tables, with the sum
/// written out, so the tests check the decoder against the specification rather than against
/// itself. UCSI returns the VDM Header and the objects, without the PD Message Header.
/// </summary>
public class DiscoverIdentityDecodeTests
{
    /// <summary>
    /// Structured VDM Header, Table 6.33: SVID 0xFF00 (PD SID) in 31-16, VDM Type 1 at 15, version
    /// major 01b (2.x) at 14-13, minor 01b (2.1) at 12-11, Command Type 01b (ACK) at 7-6, Command
    /// 1 (Discover Identity) at 4-0. 0xFF000000 + 0x8000 + 0x2000 + 0x800 + 0x40 + 0x1.
    /// </summary>
    private const uint AckHeader = 0xFF00A841;

    private static byte[] Response(params uint[] objects)
        => objects.SelectMany(BitConverter.GetBytes).ToArray();

    // ID Header, Table 6.34. Passive Cable 011b << 27 = 0x18000000, connector type USB Type-C plug
    // 11b << 21 = 0x600000, VID 0x1234.
    private const uint PassiveCableHeader = 0x18601234;

    // Passive Cable VDO, Table 6.42: HW 1 << 28, FW 2 << 24, VDO Version 000b, Type-C 10b << 18
    // = 0x80000, EPR 1 << 17 = 0x20000, latency 0001b << 13 = 0x2000, termination 00b, maximum
    // VBUS 11b << 9 = 0x600 (50V), current 10b << 5 = 0x40 (5A), speed 010b (Gen2).
    // 0x12000000 + 0x80000 + 0x20000 + 0x2000 + 0x600 + 0x40 + 0x2.
    private const uint PassiveCableVdo = 0x120A2642;

    [Fact]
    public void PassiveCableDecodes()
    {
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(AckHeader, PassiveCableHeader, 0x0000ABCD, 0x56780100, PassiveCableVdo), cablePlug: true);

        Assert.True(r.DataAvailable);
        Assert.True(r.Complete);
        Assert.Equal("ACK", r.CommandType);
        Assert.Equal("SOP'", r.Recipient);

        IdHeaderReport id = Assert.IsType<IdHeaderReport>(r.IdHeader);
        Assert.Equal("Passive Cable", id.ProductType.Meaning);
        Assert.Equal("defined", id.ProductType.Status);
        Assert.Null(id.ProductTypeDfp);   // reserved in SOP' communication
        Assert.Equal("0x1234", id.VendorId);
        Assert.Equal("USB Type-C plug", id.ConnectorType!.Meaning);
        Assert.False(id.ModalOperationSupported);

        Assert.Equal("0x0000ABCD", r.CertStatXid);
        Assert.Equal("0x5678", r.Product!.ProductId);
        Assert.Equal("0x0100", r.Product.BcdDevice);

        PassiveCableVdoReport p = Assert.IsType<PassiveCableVdoReport>(r.PassiveCable);
        Assert.Equal(1, p.HardwareVersion);
        Assert.Equal(2, p.FirmwareVersion);
        Assert.Equal("USB Type-C", p.PlugType!.Meaning);
        Assert.True(p.EprCapable);
        Assert.Contains("<10ns", p.Latency!.Meaning);
        Assert.Equal(0, p.TerminationType!.Code);
        Assert.Equal(50, p.MaxVbusVolts);
        Assert.Equal(5000, p.MaxCurrentMilliamps);
        Assert.Equal(2, p.HighestSpeed!.Code);
        Assert.Contains("Gen2", p.HighestSpeed.Meaning);
        Assert.Equal("0x120A2642", p.Raw);

        Assert.Null(r.ActiveCable);
        Assert.Null(r.VconnPoweredDevice);
        Assert.Null(r.Ufp);
        Assert.Null(r.Dfp);
        Assert.Equal(["0xFF00A841", "0x18601234", "0x0000ABCD", "0x56780100", "0x120A2642"], r.ObjectsHex);
    }

    [Fact]
    public void TheCableIsReportedAsDeclaringNotAsProven()
    {
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(AckHeader, PassiveCableHeader, 0, 0x56780100, PassiveCableVdo), cablePlug: true);

        Assert.StartsWith("The cable declares", r.Declaration);
        Assert.Contains("5A", r.Declaration);
        Assert.Contains("50V", r.Declaration);
        Assert.Contains("not proof", r.Declaration);
    }

    [Fact]
    public void DeprecatedVoltageCodesAreLabelledWithTheirEarlierMeaning()
    {
        // Maximum VBUS Voltage 01b (0x200 in place of 0x600). R3.2: "Deprecated, receiver Shall
        // assume 00b (20V)". Earlier revisions assigned 01b to 30V.
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(AckHeader, PassiveCableHeader, 0, 0x56780100, 0x120A2242), cablePlug: true);

        PassiveCableVdoReport p = r.PassiveCable!;
        Assert.Equal(1, p.MaxVbusVoltage!.Code);
        Assert.Equal("deprecated", p.MaxVbusVoltage.Status);
        Assert.Contains("30V", p.MaxVbusVoltage.Meaning);
        Assert.Contains("20V", p.MaxVbusVoltage.Meaning);
        Assert.Null(p.MaxVbusVolts);
    }

    [Fact]
    public void InvalidCurrentIsReservedAndNotAssumed()
    {
        // Current 00b (0x40 removed). R3.2 tells a receiver to assume 3A; portmark reports the code
        // as reserved and states that instruction, but does not turn it into a rating.
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(AckHeader, PassiveCableHeader, 0, 0x56780100, 0x120A2602), cablePlug: true);

        PassiveCableVdoReport p = r.PassiveCable!;
        Assert.Equal("reserved", p.CurrentHandling!.Status);
        Assert.Null(p.MaxCurrentMilliamps);
        Assert.Contains("3A", p.CurrentHandling.Meaning);
    }

    // ID Header: Active Cable 100b << 27 = 0x20000000, modal 1 << 26 = 0x4000000, Type-C plug
    // 11b << 21 = 0x600000, VID 0x05AC.
    private const uint ActiveCableHeader = 0x246005AC;

    // Active Cable VDO1, Table 6.43: VDO Version 011b << 21 = 0x600000, Type-C 10b << 18 = 0x80000,
    // latency 1000b << 13 = 0x10000, termination 11b << 11 = 0x1800, maximum VBUS 00b, SBU
    // supported (bit 8 clear), SBU passive, current 01b << 5 = 0x20, VBUS through cable 0x10,
    // SOP'' present 0x8, speed 011b. 0x600000 + 0x80000 + 0x10000 + 0x1800 + 0x20 + 0x10 + 0x8 + 0x3.
    private const uint ActiveCableVdo1 = 0x0069183B;

    // Active Cable VDO2, Table 6.44: 70C << 24, 85C << 16, U3/CLd 010b << 12 = 0x2000, re-timer
    // 1 << 9 = 0x200, USB4, USB 2.0 and USB 3.2 supported (bits 8, 5, 4 clear), two lanes 0x8, Gen 2
    // or higher 0x1. 0x46000000 + 0x550000 + 0x2000 + 0x200 + 0x8 + 0x1.
    private const uint ActiveCableVdo2 = 0x46552209;

    [Fact]
    public void ActiveCableDecodesBothVdos()
    {
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(AckHeader, ActiveCableHeader, 0, 0x00010002, ActiveCableVdo1, ActiveCableVdo2), cablePlug: true);

        Assert.True(r.Complete);
        Assert.True(r.IdHeader!.ModalOperationSupported);
        Assert.Equal("Active Cable", r.IdHeader.ProductType.Meaning);

        ActiveCableVdoReport a = Assert.IsType<ActiveCableVdoReport>(r.ActiveCable);
        Assert.Equal(3, a.VdoVersion.Code);
        Assert.Equal("defined", a.VdoVersion.Status);
        Assert.Contains("1000ns", a.Latency!.Meaning);    // active cables reuse 1000b differently
        Assert.Equal(3, a.TerminationType!.Code);
        Assert.Equal(20, a.MaxVbusVolts);
        Assert.True(a.SbuSupported);
        Assert.Equal("passive", a.SbuType!.Meaning);
        Assert.True(a.VbusThroughCable);
        Assert.Equal(3000, a.MaxCurrentMilliamps);
        Assert.True(a.SopDoublePrimeControllerPresent);
        Assert.Equal(3, a.HighestSpeed!.Code);

        Assert.Equal(70, a.MaxOperatingTemperatureCelsius);
        Assert.Equal(85, a.ShutdownTemperatureCelsius);
        Assert.Equal(2, a.U3CldPower!.Code);
        Assert.False(a.U3ToU0ThroughU3S);
        Assert.Equal("copper", a.PhysicalConnection!.Meaning);
        Assert.Equal("re-timer", a.ActiveElement!.Meaning);
        Assert.True(a.Usb4Supported);        // bit 8 clear means supported
        Assert.True(a.Usb2Supported);
        Assert.True(a.Usb32Supported);
        Assert.Equal(0, a.Usb2HubHopsConsumed);
        Assert.Equal("two lanes", a.LanesSupported!.Meaning);
        Assert.False(a.OpticallyIsolated);
        Assert.False(a.Usb4AsymmetricModeSupported);
        Assert.Equal(1, a.UsbGen!.Code);
        Assert.Equal("0x46552209", a.Raw2);

        Assert.Null(r.PassiveCable);
    }

    [Fact]
    public void CurrentIsNotReportedForACableWithoutVbus()
    {
        // VBUS Through Cable clear (0x10 removed). R3.2: current "Shall be ignored when VBUS
        // Through Cable = 0".
        ActiveCableVdoReport a = DiscoverIdentity.Decode(
            Response(AckHeader, ActiveCableHeader, 0, 0, 0x0069182B, ActiveCableVdo2), cablePlug: true).ActiveCable!;

        Assert.False(a.VbusThroughCable);
        Assert.Null(a.MaxCurrentMilliamps);
    }

    [Fact]
    public void AMissingSecondActiveCableVdoMakesTheAnswerIncomplete()
    {
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(AckHeader, ActiveCableHeader, 0, 0x00010002, ActiveCableVdo1), cablePlug: true);

        Assert.True(r.DataAvailable);
        Assert.False(r.Complete);
        Assert.NotNull(r.Reason);
        Assert.Null(r.ActiveCable!.MaxOperatingTemperatureCelsius);
        Assert.NotNull(r.ActiveCable.HighestSpeed);
    }

    [Fact]
    public void AnOlderActiveCableVdoIsNotReadWithTheCurrentLayout()
    {
        // VDO Version 000b (0x600000 removed): "Deprecated, Version 1.0". Its fields are laid out
        // by an earlier revision, so only the version is reported.
        ActiveCableVdoReport a = DiscoverIdentity.Decode(
            Response(AckHeader, ActiveCableHeader, 0, 0, 0x0009183B, ActiveCableVdo2), cablePlug: true).ActiveCable!;

        Assert.Equal("deprecated", a.VdoVersion.Status);
        Assert.Null(a.HighestSpeed);
        Assert.Null(a.MaxCurrentMilliamps);
        Assert.Null(a.MaxOperatingTemperatureCelsius);
        Assert.NotNull(a.Note);
    }

    [Fact]
    public void AVconnPoweredDeviceIsNeverDecodedAsACable()
    {
        // ID Header: VPD 110b << 27 = 0x30000000, Type-C plug 0x600000, VID 0x1234. VPD VDO, Table
        // 6.45: charge through current 1 << 14 = 0x4000 (5A), VBUS impedance 10 << 7 = 0x500
        // (20 milliohms), ground impedance 15 << 1 = 0x1E, charge through supported 0x1.
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(AckHeader, 0x30601234, 0, 0x00010002, 0x0000451F), cablePlug: true);

        Assert.Equal("VCONN Powered USB Device (VPD)", r.IdHeader!.ProductType.Meaning);
        Assert.Null(r.PassiveCable);
        Assert.Null(r.ActiveCable);

        VconnPoweredDeviceVdoReport v = Assert.IsType<VconnPoweredDeviceVdoReport>(r.VconnPoweredDevice);
        Assert.True(v.ChargeThroughSupported);
        Assert.Contains("5A", v.ChargeThroughCurrent!.Meaning);
        Assert.Equal(20, v.VbusImpedanceMilliohms);
        Assert.Equal(15, v.GroundImpedanceMilliohms);
        Assert.Equal(20, v.MaxVbusVolts);

        Assert.DoesNotContain("cable", r.Declaration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImpedancesBelowTenMilliohmsAreReserved()
    {
        // VBUS impedance code 4 (8 milliohms) << 7 = 0x200, ground code 9 << 1 = 0x12.
        VconnPoweredDeviceVdoReport v = DiscoverIdentity.Decode(
            Response(AckHeader, 0x30601234, 0, 0, 0x00004213), cablePlug: true).VconnPoweredDevice!;

        Assert.Null(v.VbusImpedanceMilliohms);
        Assert.Null(v.GroundImpedanceMilliohms);
    }

    // ID Header, SOP: host 0x80000000, device 0x40000000, PDUSB Peripheral 010b << 27 = 0x10000000,
    // modal 0x4000000, PDUSB Host 010b << 23 = 0x1000000, receptacle 10b << 21 = 0x400000, VID
    // 0x17EF.
    private const uint DrdHeader = 0xD54017EF;

    // UFP VDO, Table 6.40: version 011b << 29 = 0x60000000, USB4 0x8000000, USB 3.2 0x4000000, USB
    // 2.0 10b << 24 = 0x2000000, TBT3 alternate mode 0x8, speed 011b.
    private const uint UfpVdo = 0x6E00000B;

    // DFP VDO, Table 6.41: version 010b << 29 = 0x40000000, USB4, 3.2 and 2.0 host 0x7000000, port 1.
    private const uint DfpVdo = 0x47000001;

    [Fact]
    public void ADualRoleDeviceReturnsUfpPadAndDfp()
    {
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(AckHeader, DrdHeader, 0x00001234, 0x00AA0100, UfpVdo, 0, DfpVdo), cablePlug: false);

        Assert.Equal("SOP", r.Recipient);
        Assert.True(r.Complete);

        IdHeaderReport id = r.IdHeader!;
        Assert.True(id.UsbHostCapable);
        Assert.True(id.UsbDeviceCapable);
        Assert.True(id.ModalOperationSupported);
        Assert.Equal("PDUSB Peripheral", id.ProductType.Meaning);
        Assert.Equal("PDUSB Host", id.ProductTypeDfp!.Meaning);
        Assert.Equal("USB Type-C receptacle", id.ConnectorType!.Meaning);
        Assert.Equal("0x17EF", id.VendorId);

        UfpVdoReport u = Assert.IsType<UfpVdoReport>(r.Ufp);
        Assert.Equal(3, u.VdoVersion.Code);
        Assert.True(u.Usb4DeviceCapable);
        Assert.True(u.Usb32DeviceCapable);
        Assert.Equal(2, u.Usb20DeviceCapability!.Code);
        Assert.True(u.Tbt3AlternateModeSupported);
        Assert.False(u.ReconfiguringAlternateModesSupported);
        Assert.False(u.NonReconfiguringAlternateModesSupported);
        Assert.Equal(3, u.HighestSpeed!.Code);

        DfpVdoReport d = Assert.IsType<DfpVdoReport>(r.Dfp);
        Assert.Equal(2, d.VdoVersion.Code);
        Assert.True(d.Usb4HostCapable);
        Assert.True(d.Usb32HostCapable);
        Assert.True(d.Usb20HostCapable);
        Assert.Equal(1, d.PortNumber);

        Assert.StartsWith("The attached device declares", r.Declaration);
    }

    [Fact]
    public void ADfpOnlyProductPutsTheDfpVdoFirst()
    {
        // Figure 6.5: Product Type DFP, five objects. PDUSB Host 0x1000000 alone, VID 0x17EF.
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(AckHeader, 0x814017EF, 0, 0, DfpVdo), cablePlug: false);

        Assert.True(r.Complete);
        Assert.Null(r.Ufp);
        Assert.Equal(1, r.Dfp!.PortNumber);
    }

    [Fact]
    public void ANonZeroPadIsNotPaperedOver()
    {
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(AckHeader, DrdHeader, 0, 0, UfpVdo, 0x00000001, DfpVdo), cablePlug: false);

        Assert.NotNull(r.Ufp);
        Assert.Null(r.Dfp);
        Assert.Contains("pad", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0x20000000u, "reserved")]      // 100b: "Invalid; receiver Should ignore this field"
    [InlineData(0x30000000u, "reserved")]      // 110b
    [InlineData(0x28000000u, "deprecated")]    // 101b: "Deprecated, Alternate Mode Adapter (AMA)"
    public void UnassignedPartnerProductTypesAreNotGuessed(uint header, string status)
    {
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(AckHeader, header | 0x17EF, 0, 0, 0x6E00000B), cablePlug: false);

        Assert.Equal(status, r.IdHeader!.ProductType.Status);
        Assert.Null(r.Ufp);
        if (status == "deprecated") Assert.Contains("Alternate Mode Adapter", r.IdHeader.ProductType.Meaning);
    }

    [Fact]
    public void ADeprecatedConnectorTypeIsLabelled()
    {
        // Connector type 00b: "Deprecated, Unknown connector type".
        IdHeaderReport id = DiscoverIdentity.Decode(Response(AckHeader, 0x18001234, 0, 0, PassiveCableVdo), true).IdHeader!;

        Assert.Equal("deprecated", id.ConnectorType!.Status);
    }

    [Fact]
    public void ANakIsAnAnswerButNotAnIdentity()
    {
        // Command Type 10b << 6 = 0x80 in place of 0x40.
        DiscoverIdentityReport r = DiscoverIdentity.Decode(Response(0xFF00A881), cablePlug: false);

        Assert.False(r.DataAvailable);
        Assert.Equal("NAK", r.CommandType);
        Assert.Contains("NAK", r.Reason);
        Assert.Null(r.IdHeader);
    }

    [Fact]
    public void AnAllZeroResponseSaysNothing()
    {
        DiscoverIdentityReport r = DiscoverIdentity.Decode(new byte[16], cablePlug: true);

        Assert.False(r.DataAvailable);
        Assert.Null(r.IdHeader);
        Assert.NotNull(r.Reason);
    }

    [Fact]
    public void SomethingOtherThanDiscoverIdentityIsNotDecoded()
    {
        // SVID 0xFF01 (DisplayPort), Command 3 (Discover Modes).
        DiscoverIdentityReport r = DiscoverIdentity.Decode(Response(0xFF01A843, 0x00000405), cablePlug: false);

        Assert.False(r.DataAvailable);
        Assert.Null(r.IdHeader);
    }

    [Fact]
    public void StructuredVdmVersionOneStopsAtTheIdHeader()
    {
        // Version major 00b: 0xFF00A841 less 0x2000 (and minor 0). Its product type VDOs are laid
        // out by USB PD R3.0 V1.0, so they are kept raw rather than read with the R3.2 tables.
        DiscoverIdentityReport r = DiscoverIdentity.Decode(
            Response(0xFF008041, PassiveCableHeader, 0, 0x56780100, PassiveCableVdo), cablePlug: true);

        Assert.True(r.DataAvailable);
        Assert.Equal("0x1234", r.IdHeader!.VendorId);
        Assert.Null(r.PassiveCable);
        Assert.Contains("1.0", r.Reason);
        Assert.Equal(5, r.ObjectsHex.Count);
    }

    [Fact]
    public void ExpectedLengthComesFromTheIdHeader()
    {
        Assert.Null(DiscoverIdentity.ExpectedLength(Response(AckHeader), cablePlug: true));
        Assert.Equal(20, DiscoverIdentity.ExpectedLength(Response(AckHeader, PassiveCableHeader, 0, 0), cablePlug: true));
        Assert.Equal(24, DiscoverIdentity.ExpectedLength(Response(AckHeader, ActiveCableHeader, 0, 0), cablePlug: true));
        Assert.Equal(28, DiscoverIdentity.ExpectedLength(Response(AckHeader, DrdHeader, 0, 0), cablePlug: false));
        Assert.Equal(4, DiscoverIdentity.ExpectedLength(Response(0xFF00A881), cablePlug: false));
        Assert.Null(DiscoverIdentity.ExpectedLength(Response(0xFF008041, PassiveCableHeader), cablePlug: true));
    }

    [Fact]
    public void AFailedCableRequestDoesNotClaimTheCableIsUnmarked()
    {
        DiscoverIdentityReport r = DiscoverIdentity.FromTransfer(
            new PdMessageTransfer([], "the controller returned an error", Failed: true), cablePlug: true);

        Assert.False(r.DataAvailable);
        Assert.Contains("does not show", r.Reason);
    }

    [Fact]
    public void ATransferThatStoppedEarlyIsReportedIncomplete()
    {
        byte[] firstPage = Response(AckHeader, ActiveCableHeader, 0, 0x00010002);

        DiscoverIdentityReport r = DiscoverIdentity.FromTransfer(
            new PdMessageTransfer(firstPage, "the controller returned an error at offset 16", Failed: false), cablePlug: true);

        Assert.True(r.DataAvailable);
        Assert.False(r.Complete);
        Assert.Contains("offset 16", r.Reason);
    }
}
