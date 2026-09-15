using Portmark.Core.Model;

namespace Portmark.Core.Ucsi;

/// <summary>The bytes of one PD message read through GET_PD_MESSAGE, and why reading stopped.</summary>
/// <param name="Bytes">Every byte the controller returned, in order.</param>
/// <param name="StoppedBecause">Why reading stopped before the message ended, or null.</param>
/// <param name="Failed">True when the controller refused before returning a single byte.</param>
public sealed record PdMessageTransfer(byte[] Bytes, string? StoppedBecause, bool Failed);

/// <summary>
/// Whether to send GET_PD_MESSAGE, and how to read a message through it.
///
/// UCSI 1.2 section 4.5.20 and UCSI 2.0 section 4.5.20 define the command. With Message Offset 0
/// and a Recipient of SOP or SOP', the controller sends the PD request on the wire and returns the
/// response; a non-zero offset returns the cached copy. So the first page of every read is traffic
/// to the attached device or the cable, not a register read, and it is only sent where the
/// controller has said it supports it.
/// </summary>
public static class PdMessage
{
    /// <summary>
    /// GET_PD_MESSAGE and bmOptionalFeatures bit 8 both first appear in UCSI 1.2 (section 4.5.20,
    /// Table 4-54). Microsoft's ucmucsispec.h, which declares the UCSI 1.1 registers, stops at
    /// SET_POWER_LEVEL (0x14) and at optional feature bit 7.
    /// </summary>
    public const ushort MinimumUcsiVersion = 0x0120;

    /// <summary>
    /// Null when GET_PD_MESSAGE may be sent; otherwise the reason it is not, in words for the user.
    ///
    /// All three conditions are required, and an unknown counts as not met. Linux gates on the
    /// feature bit alone. That is not enough here: a bit that is undefined in the controller's own
    /// UCSI version is not an offer, and this machine's controller has been wedged by commands it
    /// did not expect.
    /// </summary>
    public static string? WhyNotAsk(ushort? ucsiVersion, PpmFeatureReport? features, bool? connected)
    {
        if (ucsiVersion is not ushort version || features is null)
            return "The port controller's UCSI version or optional features could not be read, so the "
                 + "attached device and cable were not asked to identify themselves.";

        bool versionOffers = version >= MinimumUcsiVersion;
        bool bitOffers = features.GetPdMessageSupported;
        string reported = UcsiProtocol.FormatVersion(version);

        if (!bitOffers)
            return "This PC's port controller does not offer PD messages ("
                 + (versionOffers
                     ? $"it reports UCSI {reported} but does not set the GET_PD_MESSAGE feature bit"
                     : $"it reports UCSI {reported}, GET_PD_MESSAGE first appears in UCSI 1.2, and the "
                     + "GET_PD_MESSAGE feature bit is not set")
                 + "), so the attached device and cable cannot be asked to identify themselves.";

        if (!versionOffers)
            return $"This PC's port controller reports UCSI {reported} and sets the GET_PD_MESSAGE feature bit, "
                 + "but that bit is only defined from UCSI 1.2, so the attached device and cable were not "
                 + "asked to identify themselves.";

        if (connected is null)
            return "Whether anything is attached could not be read, so the attached device and cable were "
                 + "not asked to identify themselves.";

        if (connected == false)
            return "Nothing is attached, so there is nothing to ask to identify itself.";

        return null;
    }

    /// <summary>
    /// Reads one message in pages no longer than MESSAGE IN, which is 16 bytes on this transport
    /// and is MAX_DATA_LENGTH (0x10) in UCSI 1.2 and 2.0 Table A-2.
    ///
    /// <paramref name="query"/> issues GET_PD_MESSAGE at an offset for a number of bytes. Reading
    /// stops on a refusal, an error, an empty page or a short one (UCSI: a Data Length below the
    /// Number of Bytes asked for means the message has no more), at <paramref name="maxBytes"/>,
    /// or once <paramref name="expectedLength"/> says the bytes so far are the whole message. The
    /// last is there so that a controller known to wedge under rapid commands is not asked for a
    /// page that cannot exist.
    /// </summary>
    public static PdMessageTransfer Read(Func<byte, byte, UcsiResult> query, int maxBytes,
                                         Func<byte[], int?>? expectedLength = null)
    {
        var bytes = new List<byte>();

        while (bytes.Count < maxBytes)
        {
            int limit = maxBytes;
            if (expectedLength?.Invoke(bytes.ToArray()) is int expected)
            {
                if (expected <= bytes.Count) break;
                limit = Math.Min(maxBytes, expected);
            }

            byte offset = (byte)bytes.Count;
            byte count = (byte)Math.Min(UcsiProtocol.MessageInSize, limit - bytes.Count);
            UcsiResult r = query(offset, count);

            string? failure = !r.Ok ? r.Error ?? "the request failed"
                : r.NotSupported ? "the controller reports GET_PD_MESSAGE as not supported"
                : r.Errored ? $"the controller returned an error at offset {offset}"
                : null;
            if (failure is not null) return new PdMessageTransfer(bytes.ToArray(), failure, Failed: bytes.Count == 0);

            if (r.Payload.Length == 0)
                return new PdMessageTransfer(bytes.ToArray(),
                                             bytes.Count == 0 ? "the controller returned no bytes" : null, Failed: false);

            // Never more than was asked for: anything past it is not part of this page.
            bytes.AddRange(r.Payload.Take(count));
            if (r.Payload.Length < count) break;
        }

        return new PdMessageTransfer(bytes.ToArray(), null, Failed: false);
    }
}

/// <summary>
/// Decodes a Discover Identity response as GET_PD_MESSAGE returns it: the Structured VDM Header
/// then the VDOs, without the PD Message Header (UCSI 2.0 section 4.5.20).
///
/// Layouts per USB PD R3.2 V1.2 section 6.4.12.3 (6.4.4.3.1 in earlier releases) with the R3.2 V1.1
/// ECN that restores Active Cable VDO Version 1.3:
///   VDM Header      Table 6.33  SVID 31-16, VDM Type 15, version major 14-13, minor 12-11,
///                               Command Type 7-6, Command 4-0
///   ID Header       Table 6.34  host 31, device 30, product type (UFP or cable plug) 29-27,
///                               modal 26, product type (DFP) 25-23, connector type 22-21, VID 15-0
///   Cert Stat       Table 6.38  XID 31-0
///   Product         Table 6.39  PID 31-16, bcdDevice 15-0
///   then, by product type (Figure 6.5): UFP VDO; DFP VDO; UFP VDO, pad, DFP VDO for a dual-role
///   product; Passive Cable VDO; Active Cable VDO1 and VDO2; VPD VDO.
///
/// Three rules run through it. A value the specification calls reserved or invalid is reported as
/// reserved with its code, never mapped to the nearest meaning. A deprecated value is labelled
/// deprecated with what it used to mean. And everything is a declaration: the device or cable
/// said it, which is not the same as it being so.
/// </summary>
public static class DiscoverIdentity
{
    /// <summary>VDM Header, ID Header, Cert Stat, Product, and at most three product type objects.</summary>
    public const int MaxBytes = 28;

    public const ushort PdSid = 0xFF00;
    private const int CommandDiscoverIdentity = 1;
    private const int CommandTypeAck = 1;

    /// <summary>
    /// How long the message is, once enough of it is known, or null. A NAK or BUSY is the header
    /// alone. An ACK is four objects plus what its ID Header's product type calls for. Structured
    /// VDM Version 1.0 lays its product type objects out differently, so no length is claimed.
    /// </summary>
    public static int? ExpectedLength(ReadOnlySpan<byte> soFar, bool cablePlug)
    {
        if (soFar.Length < 4) return null;

        uint header = U32(soFar, 0);
        if (!IsDiscoverIdentity(header)) return 4;
        if (((header >> 6) & 3) != CommandTypeAck) return 4;
        if (((header >> 13) & 3) == 0) return null;
        if (soFar.Length < 8) return null;

        return 16 + 4 * ProductTypeObjectCount(U32(soFar, 4), cablePlug);
    }

    /// <summary>Turns a GET_PD_MESSAGE read into a report, saying why when it holds no identity.</summary>
    public static DiscoverIdentityReport FromTransfer(PdMessageTransfer transfer, bool cablePlug)
    {
        if (transfer.Failed)
        {
            return new DiscoverIdentityReport
            {
                Recipient = cablePlug ? "SOP'" : "SOP",
                DataAvailable = false,
                Reason = cablePlug
                    ? $"The controller did not return the cable's Discover Identity response: {transfer.StoppedBecause}. "
                    + "UCSI reports it this way when the cable did not answer, answered that it does not support the "
                    + "request, or communication failed, and which of those happened was not asked. A cable without "
                    + "an e-marker cannot answer, but an error here does not show that this cable has none."
                    : $"The controller did not return the attached device's Discover Identity response: {transfer.StoppedBecause}. "
                    + "UCSI reports it this way when the device did not answer, answered that it does not support the "
                    + "request, or communication failed, and which of those happened was not asked.",
            };
        }

        DiscoverIdentityReport report = Decode(transfer.Bytes, cablePlug);
        if (transfer.StoppedBecause is { } why && report.DataAvailable)
            report.Reason = Join(report.Reason, $"Reading stopped because {why}");
        return report;
    }

    public static DiscoverIdentityReport Decode(ReadOnlySpan<byte> response, bool cablePlug)
    {
        var report = new DiscoverIdentityReport { Recipient = cablePlug ? "SOP'" : "SOP" };
        string who = cablePlug ? "the cable plug" : "the attached device";

        int count = response.Length / 4;
        for (int i = 0; i < count; i++) report.ObjectsHex.Add(Hex(U32(response, i * 4)));

        if (count == 0 || !response.ContainsAnyExcept((byte)0))
        {
            report.Reason = $"The controller returned no Discover Identity response from {who}.";
            return report;
        }

        uint header = U32(response, 0);
        if (!IsDiscoverIdentity(header))
        {
            report.Reason = $"The controller returned something other than a Discover Identity response (VDM Header "
                          + $"{Hex(header)}), so it was not decoded.";
            return report;
        }

        int commandType = (int)((header >> 6) & 3);
        report.CommandType = commandType switch { 0 => "REQ", 1 => "ACK", 2 => "NAK", _ => "BUSY" };

        int major = (int)((header >> 13) & 3);
        int minor = (int)((header >> 11) & 3);
        report.StructuredVdmVersion = major switch
        {
            0 => "1.0",
            1 => minor <= 1 ? $"2.{minor}" : $"2.x (minor {minor} is invalid)",
            // Table 6.33: "10b...11b - Invalid, receiver uses 01b (Version 2.x)". The specification's
            // rule, applied as written and labelled.
            _ => $"invalid major version {major}, read as 2.x as USB PD R3.2 directs",
        };

        switch (commandType)
        {
            case 2:
                report.Reason = $"{Capital(who)} answered Discover Identity with NAK, declining to identify itself.";
                return report;
            case 3:
                report.Reason = $"{Capital(who)} answered Discover Identity with BUSY: it could not answer then, and may on a later read.";
                return report;
            case 0:
                report.Reason = "The controller returned a Discover Identity request rather than a response, so it was not decoded.";
                return report;
        }

        if (count < 2)
        {
            report.Complete = false;
            report.Reason = $"{Capital(who)} acknowledged Discover Identity, but the response carried no ID Header.";
            return report;
        }

        report.DataAvailable = true;
        bool svdm1 = major == 0;
        uint id = U32(response, 4);
        report.IdHeader = DecodeIdHeader(id, cablePlug, svdm1);
        if (count >= 3) report.CertStatXid = Hex(U32(response, 8));
        if (count >= 4)
        {
            uint product = U32(response, 12);
            report.Product = new ProductVdoReport
            {
                ProductId = $"0x{product >> 16:X4}",
                BcdDevice = $"0x{product & 0xFFFF:X4}",
                Raw = Hex(product),
            };
        }

        var notes = new List<string>();
        if (response.Length % 4 != 0)
            notes.Add($"{response.Length % 4} trailing byte(s) do not make a whole object and were ignored");

        if (svdm1)
        {
            // Complete stays null: the length of a Version 1.0 response is not known from R3.2.
            notes.Add("The response uses Structured VDM Version 1.0, whose product type VDOs are laid out by USB PD "
                    + "Revision 3.0 Version 1.0 rather than the tables decoded here, so they are kept raw");
            report.Reason = Join(null, notes);
            report.Declaration = Declaration(report, cablePlug);
            return report;
        }

        byte[] objects = response.ToArray();
        uint? Obj(int index) => index < count ? U32(objects, index * 4) : null;
        int productType = (int)((id >> 27) & 7);
        int expected = 4 + ProductTypeObjectCount(id, cablePlug);
        bool layoutOk = true;

        if (cablePlug)
        {
            switch (productType)
            {
                case 3 when Obj(4) is uint passive:
                    report.PassiveCable = DecodePassiveCable(passive);
                    break;
                case 4 when Obj(4) is uint vdo1:
                    report.ActiveCable = DecodeActiveCable(vdo1, Obj(5));
                    break;
                case 6 when Obj(4) is uint vpd:
                    // A VCONN Powered USB Device answers on SOP' like a cable plug but is not a cable,
                    // and its VDO shares no layout with the cable VDOs.
                    report.VconnPoweredDevice = DecodeVpd(vpd);
                    break;
            }
        }
        else
        {
            bool ufp = HasUfpVdo(id);
            bool dfp = HasDfpVdo(id);
            if (ufp && Obj(4) is uint u) report.Ufp = DecodeUfp(u);
            if (dfp)
            {
                if (ufp && Obj(5) is uint pad && pad != 0)
                {
                    layoutOk = false;
                    notes.Add($"The pad object between the UFP and DFP VDOs is {Hex(pad)} where USB PD R3.2 requires "
                            + "zero, so the object after it was not read as a DFP VDO");
                }
                else if (Obj(ufp ? 6 : 4) is uint d)
                {
                    report.Dfp = DecodeDfp(d);
                }
            }

            if (productType == 5)
                notes.Add("The Alternate Mode Adapter product type is deprecated and USB PD R3.2 no longer defines its "
                        + "VDO, so any further objects are kept raw");
        }

        if (count < expected)
        {
            layoutOk = false;
            notes.Add($"The response ended after {count} object(s), where the ID Header's product type calls for "
                    + $"{expected}, so it is incomplete");
        }
        else if (count > expected)
        {
            notes.Add($"{count - expected} object(s) beyond what the product type calls for were kept raw and not decoded");
        }

        report.Complete = layoutOk;
        report.Reason = Join(null, notes);
        report.Declaration = Declaration(report, cablePlug);
        return report;
    }

    private static bool IsDiscoverIdentity(uint header)
        => (header >> 16) == PdSid && (header & 0x8000) != 0 && (header & 0x1F) == CommandDiscoverIdentity;

    private static bool HasUfpVdo(uint id) => ((id >> 27) & 7) is 1 or 2;
    private static bool HasDfpVdo(uint id) => ((id >> 23) & 7) is 1 or 2 or 3;

    /// <summary>Tables 6.35 to 6.37: which product types carry a VDO, and Figure 6.5 for the pad.</summary>
    private static int ProductTypeObjectCount(uint id, bool cablePlug)
    {
        if (cablePlug)
            return ((id >> 27) & 7) switch { 3 => 1, 4 => 2, 6 => 1, _ => 0 };

        bool ufp = HasUfpVdo(id), dfp = HasDfpVdo(id);
        return ufp && dfp ? 3 : ufp || dfp ? 1 : 0;
    }

    // ---- ID Header, Table 6.34 ----

    private static IdHeaderReport DecodeIdHeader(uint v, bool cablePlug, bool svdm1)
    {
        int productType = (int)((v >> 27) & 7);
        return new IdHeaderReport
        {
            UsbHostCapable = Bit(v, 31),
            UsbDeviceCapable = Bit(v, 30),
            ProductType = cablePlug ? CablePlugProductType(productType) : UfpProductType(productType),
            ModalOperationSupported = Bit(v, 26),
            // Reserved in SOP' communication, and not defined in Structured VDM Version 1.0.
            ProductTypeDfp = cablePlug || svdm1 ? null : DfpProductType((int)((v >> 23) & 7)),
            ConnectorType = svdm1 ? null : ConnectorType((int)((v >> 21) & 3)),
            VendorId = $"0x{v & 0xFFFF:X4}",
            Raw = Hex(v),
        };
    }

    private static IdentityCode UfpProductType(int code) => code switch
    {
        0 => Defined(code, "Not a UFP"),
        1 => Defined(code, "PDUSB Hub"),
        2 => Defined(code, "PDUSB Peripheral"),
        3 => Defined(code, "PSD"),
        5 => Deprecated(code, "Alternate Mode Adapter (AMA), deprecated"),
        _ => Reserved(code, 3),
    };

    private static IdentityCode CablePlugProductType(int code) => code switch
    {
        0 => Defined(code, "Not a Cable Plug/VPD"),
        3 => Defined(code, "Passive Cable"),
        4 => Defined(code, "Active Cable"),
        6 => Defined(code, "VCONN Powered USB Device (VPD)"),
        _ => Reserved(code, 3),
    };

    private static IdentityCode DfpProductType(int code) => code switch
    {
        0 => Defined(code, "Not a DFP"),
        1 => Defined(code, "PDUSB Hub"),
        2 => Defined(code, "PDUSB Host"),
        3 => Defined(code, "Power Brick"),
        4 => Deprecated(code, "Alternate Mode Controller (AMC), deprecated"),
        _ => Reserved(code, 3),
    };

    private static IdentityCode ConnectorType(int code) => code switch
    {
        0 => Deprecated(code, "unknown connector type, deprecated"),
        2 => Defined(code, "USB Type-C receptacle"),
        3 => Defined(code, "USB Type-C plug"),
        _ => Reserved(code, 2),
    };

    // ---- UFP VDO, Table 6.40 ----

    private static UfpVdoReport DecodeUfp(uint v)
    {
        int version = (int)((v >> 29) & 7);
        var report = new UfpVdoReport
        {
            VdoVersion = version switch
            {
                3 => Defined(version, "Version 1.3"),
                1 => Deprecated(version, "Version 1.1, deprecated"),
                2 => Deprecated(version, "Version 1.2, deprecated"),
                _ => Reserved(version, 3),
            },
            Raw = Hex(v),
        };

        if (version != 3)
        {
            report.Note = EarlierLayout;
            return report;
        }

        report.Usb4DeviceCapable = Bit(v, 27);
        report.Usb32DeviceCapable = Bit(v, 26);
        int usb2 = (int)((v >> 24) & 3);
        report.Usb20DeviceCapability = usb2 switch
        {
            0 => Defined(usb2, "not USB 2.0 capable"),
            1 => Defined(usb2, "USB 2.0 as a Billboard device only"),
            2 => Defined(usb2, "USB 2.0 capable"),
            _ => Reserved(usb2, 2, "USB PD R3.2 tells a receiver to take it as not USB 2.0 capable"),
        };

        report.NonReconfiguringAlternateModesSupported = Bit(v, 5);
        report.ReconfiguringAlternateModesSupported = Bit(v, 4);
        report.Tbt3AlternateModeSupported = Bit(v, 3);

        // VBUS Required and VCONN Required are reserved when no alternate mode is declared, and VCONN
        // Power is reserved unless VCONN is required.
        if (Bit(v, 5) || Bit(v, 4) || Bit(v, 3))
        {
            report.VbusRequired = !Bit(v, 6);      // 0b - Yes, 1b - No
            report.VconnRequired = Bit(v, 7);
            if (Bit(v, 7))
            {
                int power = (int)((v >> 8) & 7);
                report.VconnPower = power switch
                {
                    0 => Defined(power, "1W"),
                    1 => Defined(power, "1.5W"),
                    2 => Defined(power, "2W"),
                    3 => Defined(power, "3W"),
                    4 => Defined(power, "4W"),
                    5 => Defined(power, "5W"),
                    6 => Defined(power, "6W"),
                    _ => Reserved(power, 3),
                };
            }
        }

        report.HighestSpeed = Speed((int)(v & 7));
        return report;
    }

    // ---- DFP VDO, Table 6.41 ----

    private static DfpVdoReport DecodeDfp(uint v)
    {
        int version = (int)((v >> 29) & 7);
        var report = new DfpVdoReport
        {
            VdoVersion = version switch
            {
                2 => Defined(version, "Version 1.2"),
                1 => Deprecated(version, "Version 1.1, deprecated"),
                _ => Reserved(version, 3),
            },
            Raw = Hex(v),
        };

        if (version != 2)
        {
            report.Note = EarlierLayout;
            return report;
        }

        report.Usb4HostCapable = Bit(v, 26);
        report.Usb32HostCapable = Bit(v, 25);
        report.Usb20HostCapable = Bit(v, 24);
        report.PortNumber = (int)(v & 0x1F);
        return report;
    }

    // ---- Passive Cable VDO, Table 6.42 ----

    private static PassiveCableVdoReport DecodePassiveCable(uint v)
    {
        int version = (int)((v >> 21) & 7);
        var report = new PassiveCableVdoReport
        {
            HardwareVersion = (int)((v >> 28) & 0xF),
            FirmwareVersion = (int)((v >> 24) & 0xF),
            VdoVersion = version == 0 ? Defined(0, "Version 1.0") : Reserved(version, 3),
            Raw = Hex(v),
        };

        if (version != 0)
        {
            report.Note = EarlierLayout;
            return report;
        }

        report.PlugType = CablePlugType((int)((v >> 18) & 3));
        report.EprCapable = Bit(v, 17);
        report.Latency = PassiveLatency((int)((v >> 13) & 0xF));

        int termination = (int)((v >> 11) & 3);
        report.TerminationType = termination switch
        {
            0 => Defined(0, "VCONN not required"),
            1 => Defined(1, "VCONN required"),
            _ => Reserved(termination, 2),
        };

        (report.MaxVbusVoltage, report.MaxVbusVolts) = CableMaxVbus((int)((v >> 9) & 3));

        int current = (int)((v >> 5) & 3);
        (report.CurrentHandling, report.MaxCurrentMilliamps) = current switch
        {
            1 => (Defined(1, "3A"), 3000),
            2 => (Defined(2, "5A"), 5000),
            _ => (Reserved(current, 2, "USB PD R3.2 tells a receiver to assume 3A"), (int?)null),
        };

        report.HighestSpeed = Speed((int)(v & 7));
        return report;
    }

    // ---- Active Cable VDO1 and VDO2, Tables 6.43 and 6.44 ----

    private static ActiveCableVdoReport DecodeActiveCable(uint v, uint? vdo2)
    {
        int version = (int)((v >> 21) & 7);
        var report = new ActiveCableVdoReport
        {
            HardwareVersion = (int)((v >> 28) & 0xF),
            FirmwareVersion = (int)((v >> 24) & 0xF),
            // R3.2 V1.1 printed 000b as the current version by mistake; its ECN restores 011b and
            // marks 000b and 010b deprecated.
            VdoVersion = version switch
            {
                3 => Defined(3, "Version 1.3"),
                0 => Deprecated(0, "Version 1.0, deprecated"),
                2 => Deprecated(2, "Version 1.2, deprecated"),
                _ => Reserved(version, 3),
            },
            Raw = Hex(v),
            Raw2 = vdo2 is uint raw2 ? Hex(raw2) : null,
        };

        if (version != 3)
        {
            report.Note = EarlierLayout;
            return report;
        }

        report.PlugType = CablePlugType((int)((v >> 18) & 3));
        report.EprCapable = Bit(v, 17);
        report.Latency = ActiveLatency((int)((v >> 13) & 0xF));

        int termination = (int)((v >> 11) & 3);
        report.TerminationType = termination switch
        {
            2 => Defined(2, "one end active, one end passive, VCONN required"),
            3 => Defined(3, "both ends active, VCONN required"),
            _ => Reserved(termination, 2),
        };

        (report.MaxVbusVoltage, report.MaxVbusVolts) = CableMaxVbus((int)((v >> 9) & 3));

        report.SbuSupported = !Bit(v, 8);          // 0b - supported
        if (report.SbuSupported == true)
            report.SbuType = Bit(v, 7) ? Defined(1, "active") : Defined(0, "passive");

        report.VbusThroughCable = Bit(v, 4);
        int current = (int)((v >> 5) & 3);
        if (report.VbusThroughCable == true)
        {
            (report.CurrentHandling, report.MaxCurrentMilliamps) = current switch
            {
                1 => (Defined(1, "3A"), 3000),
                2 => (Defined(2, "5A"), 5000),
                _ => (Reserved(current, 2), (int?)null),
            };
        }
        else
        {
            report.CurrentHandling = Defined(current, "not applicable: the cable declares no end-to-end VBUS wire, "
                                                    + "and USB PD R3.2 says to ignore this field");
        }

        report.SopDoublePrimeControllerPresent = Bit(v, 3);
        report.HighestSpeed = Speed((int)(v & 7));

        if (vdo2 is not uint w) return report;

        report.MaxOperatingTemperatureCelsius = (int)((w >> 24) & 0xFF);
        report.ShutdownTemperatureCelsius = (int)((w >> 16) & 0xFF);
        int u3 = (int)((w >> 12) & 7);
        report.U3CldPower = u3 switch
        {
            0 => Defined(0, ">10mW"),
            1 => Defined(1, "5-10mW"),
            2 => Defined(2, "1-5mW"),
            3 => Defined(3, "0.5-1mW"),
            4 => Defined(4, "0.2-0.5mW"),
            5 => Defined(5, "50-200 microwatts"),
            6 => Defined(6, "under 50 microwatts"),
            _ => Reserved(u3, 3, "USB PD R3.2 tells a receiver to assume >10mW"),
        };
        report.U3ToU0ThroughU3S = Bit(w, 11);
        report.PhysicalConnection = Bit(w, 10) ? Defined(1, "optical") : Defined(0, "copper");
        report.ActiveElement = Bit(w, 9) ? Defined(1, "re-timer") : Defined(0, "re-driver");
        report.Usb4Supported = !Bit(w, 8);          // 0b - supported
        report.Usb2HubHopsConsumed = (int)((w >> 6) & 3);
        report.Usb2Supported = !Bit(w, 5);          // 0b - supported
        report.Usb32Supported = !Bit(w, 4);         // 0b - supported
        report.LanesSupported = Bit(w, 3) ? Defined(1, "two lanes") : Defined(0, "one lane");
        report.OpticallyIsolated = Bit(w, 2);
        report.Usb4AsymmetricModeSupported = Bit(w, 1);
        report.UsbGen = Bit(w, 0) ? Defined(1, "Gen 2 or higher") : Defined(0, "Gen 1");
        return report;
    }

    // ---- VCONN Powered USB Device VDO, Table 6.45 ----

    private static VconnPoweredDeviceVdoReport DecodeVpd(uint v)
    {
        int version = (int)((v >> 21) & 7);
        var report = new VconnPoweredDeviceVdoReport
        {
            HardwareVersion = (int)((v >> 28) & 0xF),
            FirmwareVersion = (int)((v >> 24) & 0xF),
            VdoVersion = version == 0 ? Defined(0, "Version 1.0") : Reserved(version, 3),
            Raw = Hex(v),
        };

        if (version != 0)
        {
            report.Note = EarlierLayout;
            return report;
        }

        // "01b..11b - Deprecated, receiver Shall assume 00b (20V)". Earlier revisions assigned them
        // to 30V, 40V and 50V, as Linux's pd_vdo.h VPD_MAX_VBUS_* still records.
        int vbus = (int)((v >> 15) & 3);
        (report.MaxVbusVoltage, report.MaxVbusVolts) = vbus switch
        {
            0 => (Defined(0, "20V"), 20),
            _ => (Deprecated(vbus, $"deprecated; earlier revisions of USB PD assigned it to {20 + 10 * vbus}V, and "
                                 + "USB PD R3.2 tells a receiver to assume 20V"), (int?)null),
        };

        report.ChargeThroughSupported = Bit(v, 0);
        if (report.ChargeThroughSupported == true)
        {
            report.ChargeThroughCurrent = Bit(v, 14) ? Defined(1, "5A capable") : Defined(0, "3A capable");

            // 2 milliohm and 1 milliohm steps; values under 10 milliohms are reserved.
            int vbusImpedance = (int)((v >> 7) & 0x3F) * 2;
            int groundImpedance = (int)((v >> 1) & 0x3F);
            report.VbusImpedanceMilliohms = vbusImpedance >= 10 ? vbusImpedance : null;
            report.GroundImpedanceMilliohms = groundImpedance >= 10 ? groundImpedance : null;
        }

        return report;
    }

    // ---- Shared fields ----

    /// <summary>USB Highest Speed, identical in the UFP, Passive Cable and Active Cable VDO1 tables.</summary>
    private static IdentityCode Speed(int code) => code switch
    {
        0 => Defined(0, "USB 2.0 only, no SuperSpeed"),
        1 => Defined(1, "USB 3.2 Gen1"),
        2 => Defined(2, "USB 3.2/USB4 Gen2"),
        3 => Defined(3, "USB4 Gen3"),
        4 => Defined(4, "USB4 Gen4"),
        _ => Reserved(code, 3),
    };

    private static IdentityCode CablePlugType(int code) => code switch
    {
        0 => Deprecated(0, "USB Type-A, deprecated"),
        1 => Deprecated(1, "USB Type-B, deprecated"),
        2 => Defined(2, "USB Type-C"),
        _ => Defined(3, "Captive"),
    };

    /// <summary>
    /// "01b..10b - Deprecated, receiver Shall assume 00b (20V)". The earlier meanings, 30V and 40V,
    /// are from the PD 3.0 tables as Linux's pd_vdo.h CABLE_MAX_VBUS_* records them. The volts are
    /// left null for a deprecated code: 20V is the specification's instruction to a receiver, not
    /// something the cable said.
    /// </summary>
    private static (IdentityCode, int?) CableMaxVbus(int code) => code switch
    {
        0 => (Defined(0, "20V"), 20),
        3 => (Defined(3, "50V"), 50),
        _ => (Deprecated(code, $"deprecated; earlier revisions of USB PD assigned it to {(code == 1 ? 30 : 40)}V, "
                             + "and USB PD R3.2 tells a receiver to assume 20V"), null),
    };

    private static readonly string[] LatencySteps =
        ["<10ns (~1m)", "10ns to 20ns (~2m)", "20ns to 30ns (~3m)", "30ns to 40ns (~4m)",
         "40ns to 50ns (~5m)", "50ns to 60ns (~6m)", "60ns to 70ns (~7m)"];

    private static IdentityCode PassiveLatency(int code) => code switch
    {
        >= 1 and <= 7 => Defined(code, LatencySteps[code - 1]),
        8 => Defined(8, ">70ns (>~7m)"),
        _ => Reserved(code, 4),
    };

    /// <summary>Active cables reuse 1000b for a far longer cable than a passive one: ~100m, not ~7m.</summary>
    private static IdentityCode ActiveLatency(int code) => code switch
    {
        >= 1 and <= 7 => Defined(code, LatencySteps[code - 1]),
        8 => Defined(8, "1000ns (~100m)"),
        9 => Defined(9, "2000ns (~200m)"),
        10 => Defined(10, "3000ns (~300m)"),
        _ => Reserved(code, 4),
    };

    private const string EarlierLayout =
        "Only the version fields were decoded. This VDO version is not the one USB PD R3.2 lays out, so "
      + "its other fields are kept raw rather than read with the wrong table.";

    // ---- The sentence ----

    private static string? Declaration(DiscoverIdentityReport r, bool cablePlug)
    {
        if (r.IdHeader is not { } id) return null;

        var parts = new List<string>();
        string ids = $"vendor ID {id.VendorId}" + (r.Product is { } p ? $", product ID {p.ProductId}" : "");

        if (r.PassiveCable is { } passive)
        {
            parts.Add("a passive cable");
            AddCable(parts, passive.HighestSpeed, passive.MaxCurrentMilliamps, passive.MaxVbusVolts, passive.EprCapable);
        }
        else if (r.ActiveCable is { } active)
        {
            parts.Add("an active cable");
            AddCable(parts, active.HighestSpeed, active.MaxCurrentMilliamps, active.MaxVbusVolts, active.EprCapable);
            if (active.PhysicalConnection is { } physical) parts.Add(physical.Meaning);
        }
        else if (r.VconnPoweredDevice is not null)
        {
            // Kept free of the word that would make this read as a cable.
            parts.Add("a VCONN Powered USB Device");
            parts.Add(ids);
            return $"The VCONN-powered device on this port declares itself {string.Join(", ", parts)}. "
                 + "This is its own statement, not proof.";
        }
        else
        {
            parts.Add($"product type \"{id.ProductType.Meaning}\"");
            if (id.ProductTypeDfp is { Code: not 0 } dfp) parts.Add($"and \"{dfp.Meaning}\" as a DFP");
        }

        if (r.Ufp?.HighestSpeed is { Status: "defined" } ufpSpeed) parts.Add($"highest speed {ufpSpeed.Meaning}");
        parts.Add(ids);

        return cablePlug
            ? $"The cable declares itself {string.Join(", ", parts)}. This is the cable's own statement, not proof of what it can carry."
            : $"The attached device declares {string.Join(", ", parts)}. This is the device's own statement, not proof.";
    }

    private static void AddCable(List<string> parts, IdentityCode? speed, int? milliamps, int? volts, bool? epr)
    {
        if (speed is { Status: "defined" }) parts.Add($"highest speed {speed.Meaning}");
        if (milliamps is int ma) parts.Add($"{ma / 1000}A");
        if (volts is int v) parts.Add($"maximum VBUS {v}V");
        if (epr == true) parts.Add("EPR capable");
    }

    // ---- Helpers ----

    private static IdentityCode Defined(int code, string meaning) => new() { Code = code, Meaning = meaning, Status = "defined" };
    private static IdentityCode Deprecated(int code, string meaning) => new() { Code = code, Meaning = meaning, Status = "deprecated" };

    private static IdentityCode Reserved(int code, int width, string? instruction = null) => new()
    {
        Code = code,
        Meaning = $"reserved: USB PD R3.2 gives {Convert.ToString(code, 2).PadLeft(width, '0')}b no meaning"
                + (instruction is null ? "" : $", and {instruction}"),
        Status = "reserved",
    };

    private static bool Bit(uint v, int bit) => ((v >> bit) & 1) != 0;
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BitConverter.ToUInt32(data.Slice(offset, 4));
    private static string Hex(uint v) => $"0x{v:X8}";
    private static string Capital(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    private static string? Join(string? existing, IEnumerable<string> sentences)
    {
        string[] all = (existing is null ? [] : new[] { existing.TrimEnd('.') }).Concat(sentences).ToArray();
        return all.Length == 0 ? null : string.Join(". ", all) + ".";
    }

    private static string? Join(string? existing, string sentence) => Join(existing, [sentence]);
}
