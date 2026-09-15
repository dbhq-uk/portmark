using Microsoft.Win32;
using Portmark.Core.Model;
using Portmark.Core.Ucsi;

namespace Portmark.Core;

/// <summary>
/// The single entry point: work out whether this machine can answer the question, and if it can,
/// answer it for every connector.
/// </summary>
public static class PortmarkReader
{
    public static PortmarkReport Read()
    {
        var report = new PortmarkReport { Machine = ReadMachine() };

        // Billboard descriptors need no setup and no rights, so read them first and unconditionally.
        // On hardware whose controller declines to identify alternate modes, this is the only
        // evidence of video capability available, and it is better evidence than UCSI would give.
        report.Billboards = Usb.BillboardReader.FindAll();

        UcsiConnection? connection = DetectCapability(report.Capability);

        if (connection is null || report.Capability.Status != CapabilityStatus.Ok)
            return report;

        // The batteries' own percentage and rate when a battery device answered; otherwise the
        // percentage from GetSystemPowerStatus, as before.
        Power.BatteryFlow? batteryFlow = Power.BatteryTelemetry.Summarise(report.Machine.Batteries);
        int? batteryPercent = batteryFlow?.ChargePercent ?? report.Machine.BatteryPercent;

        int connectors = report.Capability.ConnectorCount ?? 0;
        for (byte index = 1; index <= connectors; index++)
            report.Connectors.Add(ReadConnector(connection, index, report.Capability.Features,
                                                batteryPercent, batteryFlow));

        return report;
    }

    /// <summary>
    /// Decides, and explains, whether cable data is reachable. Runs before any answer is produced,
    /// so that a machine which cannot do this is told so rather than shown an empty result.
    /// </summary>
    public static UcsiConnection? DetectCapability(CapabilityReport capability)
    {
        IReadOnlyList<string> devices = UcsiDevice.FindDevices();
        capability.UcmDevicePresent = devices.Count > 0;
        capability.UcmDeviceInstanceId = devices.FirstOrDefault();

        if (!capability.UcmDevicePresent)
        {
            capability.Status = CapabilityStatus.Unsupported;
            capability.Explanation =
                "This PC has no USB-C connector manager device, so Windows holds no cable "
              + "information to read. This is a property of the hardware and firmware, and no "
              + "software can work around it.";
            return null;
        }

        string instanceId = capability.UcmDeviceInstanceId!;
        capability.TestInterfaceEnabled = UcsiDevice.IsTestInterfaceEnabled(instanceId);

        UcsiConnection? connection = UcsiConnection.TryOpen();
        capability.TestInterfacePublished = connection is not null;

        if (connection is null)
        {
            capability.Status = CapabilityStatus.NeedsSetup;
            capability.Explanation = capability.TestInterfaceEnabled
                ? "The port controller interface is switched on but Windows has not published it "
                + "yet. This normally clears after the device restarts."
                : "Windows may be able to report more about your ports, but the interface that "
                + "exposes it is switched off by default. Whether this PC can report cable details "
                + "specifically is not knowable until it is switched on: many controllers cannot.";
            capability.Remedy = capability.TestInterfaceEnabled
                ? "Restart the USB-C device, or reboot."
                : "Run 'portmark enable' as an administrator. This is a one-time step, and "
                + "'portmark disable' reverses it.";
            return null;
        }

        UcsiResult state = connection.ReadState();
        if (!state.Ok)
        {
            capability.Status = CapabilityStatus.Unsupported;
            capability.Explanation =
                $"The port controller interface is present but did not respond: {state.Error}";
            return null;
        }

        ushort version = BitConverter.ToUInt16(state.Payload, UcsiProtocol.OffsetVersion);
        capability.UcsiVersion = UcsiProtocol.FormatVersion(version);

        UcsiResult caps = connection.Execute(UcsiProtocol.CmdGetCapability);
        if (!caps.Ok || caps.Payload.Length < 5)
        {
            capability.Status = CapabilityStatus.Unsupported;
            capability.Explanation =
                "The port controller did not report its capabilities, so its connectors cannot be "
              + "enumerated.";
            return null;
        }

        capability.ConnectorCount = Capability.ConnectorCount(caps.Payload);
        capability.Features = Capability.Decode(caps.Payload);
        capability.Status = CapabilityStatus.Ok;

        // Say up front whether cable data is obtainable at all. A controller that does not
        // advertise CableDetailsAvailable will never return it, on any cable, ever.
        capability.Explanation = capability.Features?.CableDetailsAvailable == false
            ? $"Reading {capability.ConnectorCount} connector(s) over UCSI {capability.UcsiVersion}. "
            + "This PC's port controller does not report cable information, so port and power "
            + "details are available but cable details are not."
            : $"Reading {capability.ConnectorCount} connector(s) over UCSI {capability.UcsiVersion}.";

        if (capability.Features?.CableDetailsAvailable == false)
            capability.Remedy = "Nothing can be done about this in software. It is a limitation of "
                              + "the firmware on this PC, not of the cable you have plugged in.";

        return connection;
    }

    private static ConnectorReport ReadConnector(UcsiConnection connection, byte index,
                                                PpmFeatureReport? features, int? batteryPercent = null,
                                                Power.BatteryFlow? batteryFlow = null)
    {
        bool cableDetailsAvailable = features?.CableDetailsAvailable ?? true;
        bool pdoDetailsAvailable = features?.PowerDataObjectDetailsAvailable ?? false;
        bool alternateModeDetailsAvailable = features?.AlternateModeDetailsAvailable ?? false;

        var report = new ConnectorReport { Index = index };

        UcsiResult caps = connection.ExecuteForConnector(UcsiProtocol.CmdGetConnectorCapability, index);
        if (caps.Ok && caps.Payload.Length >= 2)
            report.Capability = ConnectorCapability.Decode(caps.Payload);

        UcsiResult camSupported = connection.ExecuteForConnector(UcsiProtocol.CmdGetCamSupported, index);
        if (camSupported.Ok && camSupported.Payload.Length >= 1)
            report.SupportedAlternateModeBitmap = camSupported.Payload[0];

        // The raw byte is recorded; whether it names a mode is decided later, with the partner's
        // list in hand. 0xFF is UCSI's "no mode" and is passed through as such. This controller
        // returns 0 for an empty port too, which is why the byte is never enough on its own. Null
        // means the read failed, which is a different thing from "none".
        UcsiResult currentCam = connection.ExecuteForConnector(UcsiProtocol.CmdGetCurrentCam, index);
        byte? currentCamByte = null;
        if (currentCam.Ok)
        {
            report.Raw.CurrentCamHex = Convert.ToHexString(currentCam.Payload);
            report.Raw.CurrentCamCci = $"0x{currentCam.Cci:X8}";
            if (!currentCam.Errored && !currentCam.NotSupported && currentCam.Payload.Length >= 1)
                currentCamByte = currentCam.Payload[0];
        }

        UcsiResult status = connection.ExecuteForConnector(UcsiProtocol.CmdGetConnectorStatus, index);
        if (status.Ok)
        {
            report.Raw.ConnectorStatusHex = Convert.ToHexString(status.Payload);
            report.Raw.ConnectorStatusCci = $"0x{status.Cci:X8}";
            ConnectorStatus.Apply(status.Payload, report);
        }
        else
        {
            report.Connected = null;
        }

        if (alternateModeDetailsAvailable) ReadAlternateModes(connection, index, report, currentCamByte);

        report.Cable = cableDetailsAvailable
            ? ReadCable(connection, index, report)
            : new CableReport
            {
                DataAvailable = false,
                Reason = "This PC's port controller does not report cable information. It does not "
                       + "advertise the cable details capability, so no cable can be identified "
                       + "here regardless of which cable is plugged in.",
                VideoNote = "Not determinable from the cable. UCSI does not report a cable's video capability; what the port and the attached device offer is under alternate modes.",
            };
        report.Power = pdoDetailsAvailable
            ? ReadPower(connection, index, report)
            : new PowerReport
            {
                DataAvailable = false,
                Reason = "This PC's port controller does not report power delivery details.",
            };

        // Only when the cable could not speak for itself. If it did, its own words stand, and a
        // deduction alongside them would be noise at best and a contradiction at worst.
        report.Cable.Inferred = CableInference.FromConnector(report);

        bool consuming = report.PowerDirection == "consuming";
        report.Power.IsUnderNegotiated = ChargeDiagnostic.IsUnderNegotiated(
            report.Power.Negotiated?.NegotiatedPowerMilliwatts, report.Power.MaxAvailableMilliwatts, consuming);
        report.Power.PowerDiagnosis = ChargeDiagnostic.Explain(
            report.Power.Negotiated?.NegotiatedPowerMilliwatts, report.Power.MaxAvailableMilliwatts, consuming,
            report.BatteryChargingStatusCode, batteryPercent, batteryFlow);

        report.Summary = Summarise(report);
        return report;
    }

    /// <summary>
    /// Lists the modes the port can enter and the modes the partner offers, then says which is in
    /// use. The interpretation lives in <see cref="AlternateModes.Interpret"/> so it can be tested
    /// without a controller; this method only issues the reads and records every exchange.
    /// </summary>
    private static void ReadAlternateModes(UcsiConnection connection, byte index, ConnectorReport report,
                                           byte? currentCam)
    {
        report.SupportedAlternateModes = EnumerateAlternateModes(connection, index, report, recipient: 0);
        if (report.Connected == true)
            report.PartnerAlternateModes = EnumerateAlternateModes(connection, index, report, recipient: 1);

        (report.AlternateModeNote, report.ActiveAlternateMode, report.ActiveAlternateModeConfirmed) = AlternateModes.Interpret(
            report.SupportedAlternateModes, report.PartnerAlternateModes, report.Connected, currentCam,
            report.PartnerAlternateModeFlag);
        report.ActiveAlternateModeIndex = report.ActiveAlternateMode?.Offset;
    }

    /// <summary>Every request is a read, and every request is recorded before it is interpreted.</summary>
    private static AlternateModeListReport EnumerateAlternateModes(UcsiConnection connection, byte index,
                                                                   ConnectorReport report, byte recipient)
        => AlternateModes.Enumerate(offset =>
        {
            ulong control = UcsiProtocol.GetAlternateModes(recipient, index, offset, 1);
            UcsiResult r = connection.Execute(UcsiProtocol.CmdGetAlternateModes, control);
            report.Raw.AlternateModeExchanges.Add(new UcsiExchangeReport
            {
                Command = $"GET_ALTERNATE_MODES recipient={recipient} offset={offset}",
                ControlHex = $"0x{control:X12}",
                Cci = r.Ok ? $"0x{r.Cci:X8}" : null,
                PayloadHex = r.Ok ? Convert.ToHexString(r.Payload) : null,
                Error = r.Error,
            });
            return r;
        });

    /// <summary>
    /// Reads what the attached supply offers, and what was actually negotiated. This works on
    /// controllers that cannot report cable data, and answers the most practically useful
    /// question a user has: how much power can this thing actually deliver?
    /// </summary>
    private static PowerReport ReadPower(UcsiConnection connection, byte index, ConnectorReport report)
    {
        var power = new PowerReport();

        PowerDataObject.SourceList partnerSource = ReadSourceList(connection, index, report, partner: true);
        power.PartnerSource = partnerSource.Objects;

        PowerDataObject.SourceList localSource = ReadSourceList(connection, index, report, partner: false);
        power.LocalSource = localSource.Objects;

        // Every page that was accepted, in order, so decoding this hex again gives the same
        // positions. Each individual answer, refused ones included, is in PdoExchanges. When no
        // page was accepted the first answer's bytes are still kept here, as they always were: an
        // all-zero list from a charger port that is powering a dock is itself worth seeing.
        string? firstPartnerPage = report.Raw.PdoExchanges
            .FirstOrDefault(e => e.Command.StartsWith("GET_PDOS partner", StringComparison.Ordinal))?.PayloadHex;
        report.Raw.PartnerSourcePdosHex = partnerSource.Bytes.Length > 0
            ? Convert.ToHexString(partnerSource.Bytes)
            : string.IsNullOrEmpty(firstPartnerPage) ? null : firstPartnerPage;

        if (report.Raw.ConnectorStatusHex is not null && report.Connected == true)
        {
            byte[] status = Convert.FromHexString(report.Raw.ConnectorStatusHex);
            if (status.Length >= 8)
            {
                uint rdo = BitConverter.ToUInt32(status, 4);
                power.Negotiated = PowerDataObject.DecodeRequest(rdo, SourceSideObjects(power, report.PowerDirection));
            }
        }

        power.MaxAvailableMilliwatts = PowerDataObject.OfferCeilingMilliwatts(power.PartnerSource);

        power.DataAvailable = power.PartnerSource.Count > 0 || power.LocalSource.Count > 0;
        if (!power.DataAvailable)
            power.Reason = "Nothing attached to this port is advertising power delivery objects.";

        return power;
    }

    private static CableReport ReadCable(UcsiConnection connection, byte index, ConnectorReport report)
    {
        UcsiResult cable = connection.ExecuteForConnector(UcsiProtocol.CmdGetCableProperty, index);

        if (!cable.Ok)
        {
            return new CableReport
            {
                DataAvailable = false,
                Reason = $"The cable query failed: {cable.Error}",
                VideoNote = "Not determinable from the cable. UCSI does not report a cable's video capability; what the port and the attached device offer is under alternate modes.",
            };
        }

        report.Raw.CablePropertyHex = Convert.ToHexString(cable.Payload);
        report.Raw.CablePropertyCci = $"0x{cable.Cci:X8}";

        if (cable.NotSupported)
        {
            return new CableReport
            {
                DataAvailable = false,
                Reason = "This port controller does not implement the cable query.",
                VideoNote = "Not determinable from the cable. UCSI does not report a cable's video capability; what the port and the attached device offer is under alternate modes.",
            };
        }

        if (!cable.NoPayload) return CableProperty.Decode(cable.Payload);

        // The command completed and returned nothing. Ask the controller whether that was a
        // refusal or a genuine "there is nothing here", rather than assuming either.
        UcsiResult error = connection.Execute(UcsiProtocol.CmdGetErrorStatus);
        bool unrecognised = error.Ok && ErrorStatus.IsUnrecognisedCommand(error.Payload);

        string reason = unrecognised
            ? "This port controller does not implement the cable query, so no cable can be identified."
            : report.Connected == false
                ? "Nothing is attached to this port."
                : "The cable did not describe itself, which usually means it carries no e-marker. "
                + "Cables rated at or below 3A are not required to have one.";

        return CableProperty.Decode([], reason);
    }

    /// <summary>One side's source list, with every GET_PDOS exchange recorded before it is decoded.</summary>
    private static PowerDataObject.SourceList ReadSourceList(UcsiConnection connection, byte index,
                                                             ConnectorReport report, bool partner)
        => PowerDataObject.ReadSourceList((offset, numberMinusOne) =>
        {
            ulong control = UcsiProtocol.GetPdos(index, partner, offset, numberMinusOne, source: true);
            UcsiResult r = connection.Execute(UcsiProtocol.CmdGetPdos, control);
            report.Raw.PdoExchanges.Add(new UcsiExchangeReport
            {
                Command = $"GET_PDOS {(partner ? "partner" : "local")} source offset={offset} count={numberMinusOne + 1}",
                ControlHex = $"0x{control:X12}",
                Cci = r.Ok ? $"0x{r.Cci:X8}" : null,
                PayloadHex = r.Ok ? Convert.ToHexString(r.Payload) : null,
                Error = r.Error,
            });
            return r;
        });

    /// <summary>
    /// The list a Request Data Object's position counts into, which is the source's. When this PC
    /// supplies, the attached device asked this PC; when it consumes, it asked the attached supply.
    /// The first version always used the partner's list, so on a port this PC was powering it named
    /// an object the request never selected. With the direction unknown neither list can be chosen.
    ///
    /// This PC's list is GET_PDOS "current supported source capabilities", which UCSI does not
    /// promise is byte-for-byte what was advertised on this connector.
    /// </summary>
    public static List<PowerObjectReport> SourceSideObjects(PowerReport power, string? direction) => direction switch
    {
        "supplying" => power.LocalSource,
        "consuming" => power.PartnerSource,
        _ => [],
    };

    /// <summary>The plain English one-liner, stating only what was actually reported.</summary>
    public static string Summarise(ConnectorReport report)
    {
        if (report.Connected is null) return "Port state could not be read.";
        if (report.Connected == false) return "Nothing attached.";

        var parts = new List<string>();
        if (report.PartnerType is not null) parts.Add(report.PartnerType);
        if (report.PowerOperationMode is not null) parts.Add($"over {report.PowerOperationMode}");
        if (report.PowerDirection is not null)
            parts.Add(report.PowerDirection == "supplying" ? "supplying power" : "drawing power");

        string attached = parts.Count > 0 ? string.Join(", ", parts) : "Something attached";

        // Power is the most useful thing we can say, and is available on controllers that cannot
        // report cable data at all. Which side is the source decides the words: while this PC
        // supplies, the attached device is not "the supply" and nothing is being drawn from it,
        // which is what the first version said on a port powering a dock.
        if (report.PowerDirection == "supplying")
        {
            if (report.Power.DataAvailable
                && PowerDataObject.OfferCeilingMilliwatts(report.Power.LocalSource) is int localMw && localMw > 0)
            {
                string supplied = report.Power.Negotiated is { SelectedKind: not null } n
                    ? $" and is supplying {n.Display}"
                    : "";
                parts.Add($"this PC offers up to {localMw / 1000.0:0.#}W{supplied}");
                attached = string.Join(", ", parts);
            }
        }
        else if (report.Power.DataAvailable && report.Power.MaxAvailableMilliwatts is int mw && mw > 0)
        {
            string offered = $"supply offers up to {mw / 1000.0:0.#}W";
            string negotiated = report.Power.Negotiated is { SelectedKind: not null } n ? $", drawing {n.Display}" : "";
            parts.Add(offered + negotiated);
            attached = string.Join(", ", parts);
        }

        CableReport cable = report.Cable;
        if (!cable.DataAvailable)
            return cable.Inferred?.MinimumCurrentRatingMilliamps is int rating
                ? $"{attached}. Cable: not reported by this PC, but the supply's offer means it is rated "
                + $"for at least {rating / 1000.0:0.##}A, if the supply follows USB Power Delivery."
                : $"{attached}. Cable: not reported by this PC.";

        var cableParts = new List<string>();
        if (cable.MaxWattsAt20Volts is int watts) cableParts.Add($"{watts}W");
        else if (cable.CurrentCapabilityMilliamps is int ma) cableParts.Add($"{ma} mA");
        if (cable.Speed is not null) cableParts.Add(cable.Speed.Display);

        string cableText = cableParts.Count > 0 ? string.Join(", ", cableParts) : "identified";
        return $"{attached}. Cable: {cableText}.";
    }

    private static MachineReport ReadMachine()
    {
        using RegistryKey? bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using RegistryKey? cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

        // Read-only battery class queries. No wait: a slot with no battery answers at once.
        List<BatteryReport> batteries = Power.BatteryTelemetry.ReadAll(out string? batteriesNote);

        return new MachineReport
        {
            Manufacturer = bios?.GetValue("SystemManufacturer")?.ToString(),
            Model = bios?.GetValue("SystemProductName")?.ToString(),
            BiosVersion = bios?.GetValue("BIOSVersion")?.ToString(),
            OsVersion = $"{Environment.OSVersion.Version.Major}.0."
                      + $"{cv?.GetValue("CurrentBuild")}.{cv?.GetValue("UBR")}",
            OsDisplayVersion = cv?.GetValue("DisplayVersion")?.ToString(),
            BatteryPercent = Native.PowerStatus.BatteryPercent(),
            Batteries = batteries,
            BatteriesNote = batteriesNote,
        };
    }
}
