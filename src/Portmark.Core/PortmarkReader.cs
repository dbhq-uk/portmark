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

        int connectors = report.Capability.ConnectorCount ?? 0;
        bool cableDetails = report.Capability.Features?.CableDetailsAvailable ?? true;
        bool pdoDetails = report.Capability.Features?.PowerDataObjectDetailsAvailable ?? false;
        for (byte index = 1; index <= connectors; index++)
            report.Connectors.Add(ReadConnector(connection, index, cableDetails, pdoDetails));

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
                                                bool cableDetailsAvailable, bool pdoDetailsAvailable)
    {
        var report = new ConnectorReport { Index = index };

        UcsiResult caps = connection.ExecuteForConnector(UcsiProtocol.CmdGetConnectorCapability, index);
        if (caps.Ok && caps.Payload.Length >= 2)
            report.Capability = ConnectorCapability.Decode(caps.Payload);

        UcsiResult camSupported = connection.ExecuteForConnector(UcsiProtocol.CmdGetCamSupported, index);
        if (camSupported.Ok && camSupported.Payload.Length >= 1)
            report.SupportedAlternateModeBitmap = camSupported.Payload[0];

        UcsiResult currentCam = connection.ExecuteForConnector(UcsiProtocol.CmdGetCurrentCam, index);
        if (currentCam.Ok && currentCam.Payload.Length >= 1 && currentCam.Payload[0] != 0xFF)
        {
            report.ActiveAlternateModeIndex = currentCam.Payload[0];
            report.AlternateModeNote =
                "An alternate mode is active on this port, but this controller declines "
              + "GET_ALTERNATE_MODES, so which mode it is cannot be determined. Video capability "
              + "is therefore unknown rather than absent.";
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

        report.Cable = cableDetailsAvailable
            ? ReadCable(connection, index, report)
            : new CableReport
            {
                DataAvailable = false,
                Reason = "This PC's port controller does not report cable information. It does not "
                       + "advertise the cable details capability, so no cable can be identified "
                       + "here regardless of which cable is plugged in.",
                VideoNote = "Not determinable. UCSI does not report video capability.",
            };
        report.Power = pdoDetailsAvailable
            ? ReadPower(connection, index, report)
            : new PowerReport
            {
                DataAvailable = false,
                Reason = "This PC's port controller does not report power delivery details.",
            };

        report.Summary = Summarise(report);
        return report;
    }

    /// <summary>
    /// Reads what the attached supply offers, and what was actually negotiated. This works on
    /// controllers that cannot report cable data, and answers the most practically useful
    /// question a user has: how much power can this thing actually deliver?
    /// </summary>
    private static PowerReport ReadPower(UcsiConnection connection, byte index, ConnectorReport report)
    {
        var power = new PowerReport();

        UcsiResult partnerSource = connection.Execute(
            UcsiProtocol.CmdGetPdos, UcsiProtocol.GetPdos(index, partner: true, 0, 3, source: true));
        if (partnerSource.Ok && partnerSource.Payload.Length >= 4)
            power.PartnerSource = PowerDataObject.DecodeAll(partnerSource.Payload);

        UcsiResult localSource = connection.Execute(
            UcsiProtocol.CmdGetPdos, UcsiProtocol.GetPdos(index, partner: false, 0, 3, source: true));
        if (localSource.Ok && localSource.Payload.Length >= 4)
            power.LocalSource = PowerDataObject.DecodeAll(localSource.Payload);

        report.Raw.PartnerSourcePdosHex = partnerSource.Ok && partnerSource.Payload.Length > 0
            ? Convert.ToHexString(partnerSource.Payload)
            : null;

        if (report.Raw.ConnectorStatusHex is not null && report.Connected == true)
        {
            byte[] status = Convert.FromHexString(report.Raw.ConnectorStatusHex);
            if (status.Length >= 8)
            {
                uint rdo = BitConverter.ToUInt32(status, 4);
                power.Negotiated = PowerDataObject.DecodeRequest(rdo, power.PartnerSource);
            }
        }

        // Only count objects we actually understood.
        List<PowerObjectReport> usable = power.PartnerSource.Where(p => p.Kind != "unrecognised").ToList();
        power.MaxAvailableMilliwatts = usable.Count > 0 ? usable.Max(p => p.MaxPowerMilliwatts ?? 0) : null;

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
                VideoNote = "Not determinable. UCSI does not report video capability.",
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
                VideoNote = "Not determinable. UCSI does not report video capability.",
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
                : "The cable carries no e-marker, so it cannot describe itself. Cables rated at or "
                + "below 3A are not required to have one.";

        return CableProperty.Decode([], reason);
    }

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
        // report cable data at all.
        if (report.Power.DataAvailable && report.Power.MaxAvailableMilliwatts is int mw && mw > 0)
        {
            string offered = $"supply offers up to {mw / 1000.0:0.#}W";
            string negotiated = report.Power.Negotiated is { } n ? $", drawing {n.Display}" : "";
            parts.Add(offered + negotiated);
            attached = string.Join(", ", parts);
        }

        CableReport cable = report.Cable;
        if (!cable.DataAvailable) return $"{attached}. Cable: not reported by this PC.";

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

        return new MachineReport
        {
            Manufacturer = bios?.GetValue("SystemManufacturer")?.ToString(),
            Model = bios?.GetValue("SystemProductName")?.ToString(),
            BiosVersion = bios?.GetValue("BIOSVersion")?.ToString(),
            OsVersion = $"{Environment.OSVersion.Version.Major}.0."
                      + $"{cv?.GetValue("CurrentBuild")}.{cv?.GetValue("UBR")}",
            OsDisplayVersion = cv?.GetValue("DisplayVersion")?.ToString(),
        };
    }
}
