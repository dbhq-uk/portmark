using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UcsiProbe.Native;

namespace UcsiProbe.Probes;

/// <summary>
/// Attempts to reach the UCSI PPM from user mode: open a device interface, then issue the
/// read-only UCSI probe commands. Every call is recorded in the report whether or not it works.
/// </summary>
internal static class UcsiAccess
{
    /// <summary>
    /// Interface GUIDs worth trying, most promising first.
    ///
    /// The two "in-box private" GUIDs were recovered empirically from the .rdata section of the
    /// shipping UcmUcsiCx.sys on this machine: they are the only GUIDs in that binary referenced
    /// by no other driver on the system, which makes them candidates for its own interfaces. The
    /// WDK sample GUID is included for completeness; it is absent from every driver binary here.
    /// </summary>
    internal static readonly (string Name, Guid Guid)[] CandidateInterfaces =
    [
        ("UcmUcsiCx private A (0d3bc324)", new Guid("0d3bc324-0125-4e95-b25a-7b393333ea9a")),
        ("UcmUcsiCx private B (c500c63a)", new Guid("c500c63a-6efe-433b-84a7-c0740d5dc97f")),
        ("WDK sample GUID_DEVINTERFACE_UCSI_TEST", Ucsi.GUID_DEVINTERFACE_UCSI_TEST),
        ("Ucmcx published A (4cedf9cf)", Ucsi.GUID_DEVINTERFACE_UCM_A),
        ("Ucmcx published B (ae05a169)", Ucsi.GUID_DEVINTERFACE_UCM_B),
    ];

    /// <summary>Symbolic-link paths worth trying directly.</summary>
    internal static readonly string[] CandidateDosPaths =
    [
        @"\\.\USB Type C",   // from DosDeviceName in the Device Parameters key of ACPI\USBC000\0
        @"\\.\UCSI",
        @"\\.\UcmUcsiCx",
    ];

    private static readonly (string Label, uint Access)[] AccessModes =
    [
        ("read|write", Win32.GENERIC_READ | Win32.GENERIC_WRITE),
        ("read", Win32.GENERIC_READ),
        ("none (metadata)", 0),
    ];

    /// <summary>
    /// Tries every interface path and DOS name, and on any handle that opens, runs the UCSI probe.
    /// Returns true when cable property bytes were successfully read.
    /// </summary>
    internal static bool TryAll(Report report, string stage)
    {
        bool gotCableProperty = false;

        foreach ((string name, Guid guid) in CandidateInterfaces)
        {
            List<string> paths = Devices.EnumerateInterfacePaths(guid, out int enumError);
            report.Add(new Attempt
            {
                Stage = stage,
                Api = "SetupDiEnumDeviceInterfaces",
                Target = $"{name} {{{guid}}}",
                Success = paths.Count > 0,
                Win32Error = enumError,
                Error = paths.Count > 0 ? null : "no present interface instances for this class",
                Result = paths.Count > 0 ? string.Join(" ; ", paths) : "0 instances",
            });

            foreach (string path in paths)
                gotCableProperty |= ProbePath(report, stage, path, name);
        }

        foreach (string dos in CandidateDosPaths)
            gotCableProperty |= ProbePath(report, stage, dos, "DOS device name");

        return gotCableProperty;
    }

    private static bool ProbePath(Report report, string stage, string path, string label)
    {
        foreach ((string accessLabel, uint access) in AccessModes)
        {
            SafeFileHandle h = Win32.CreateFile(
                path, access, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero,
                Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            int err = Marshal.GetLastWin32Error();

            bool opened = !h.IsInvalid;
            report.Add(new Attempt
            {
                Stage = stage,
                Api = "CreateFileW",
                Target = path,
                Detail = $"{label}, access={accessLabel}",
                Success = opened,
                Win32Error = opened ? 0 : err,
                Error = opened ? null : Win32.Describe(err),
                Result = opened ? "handle opened" : null,
            });

            if (!opened) { h.Dispose(); continue; }

            using (h)
            {
                return RunUcsiProbe(report, stage, h, path);
            }
        }
        return false;
    }

    /// <summary>
    /// Issues the read-only UCSI sequence against an open handle. Only GET_* commands are sent,
    /// plus the mandatory ACK_CC_CI handshake that the UCSI protocol requires after a command
    /// completes. No SET_*, no PPM_RESET, no connector reset.
    /// </summary>
    private static bool RunUcsiProbe(Report report, string stage, SafeFileHandle h, string path)
    {
        // GET_CCI is a pure read: if the handle speaks UCSI at all, this responds.
        bool cciWorks = TryIoctl(report, stage, h, path, Ucsi.IOCTL_UCSI_GET_CCI, "IOCTL_UCSI_GET_CCI",
                                 [], 4, out _);

        // GET_CAPABILITY first: harmless, and proves the command path end to end.
        TrySendAndRead(report, stage, h, path, Ucsi.CMD_GET_CAPABILITY,
                       Ucsi.Control(Ucsi.CMD_GET_CAPABILITY), out _);

        bool any = false;
        for (byte connector = 1; connector <= 4; connector++)
        {
            if (TrySendAndRead(report, stage, h, path, Ucsi.CMD_GET_CABLE_PROPERTY,
                               Ucsi.ForConnector(Ucsi.CMD_GET_CABLE_PROPERTY, connector), out byte[]? data)
                && data is not null)
            {
                report.CablePropertyBytes.Add($"connector {connector}: {Convert.ToHexString(data)}");
                any = true;
            }

            TrySendAndRead(report, stage, h, path, Ucsi.CMD_GET_CONNECTOR_STATUS,
                           Ucsi.ForConnector(Ucsi.CMD_GET_CONNECTOR_STATUS, connector), out _);
        }

        if (!any && cciWorks)
            report.Notes.Add($"{path}: handle opened and GET_CCI responded, but GET_CABLE_PROPERTY returned nothing.");

        return any;
    }

    /// <summary>SEND_COMMAND, then GET_MESSAGE to collect the response, then acknowledge.</summary>
    private static bool TrySendAndRead(
        Report report, string stage, SafeFileHandle h, string path,
        byte command, ulong control, out byte[]? data)
    {
        data = null;
        string name = Ucsi.CommandName(command);

        // The exact input layout of the test IOCTL is not publicly documented; try the bare
        // 8-byte UCSI CONTROL first, then a 16-byte form, recording whichever the driver accepts.
        foreach (int inputSize in new[] { 8, 16 })
        {
            byte[] input = new byte[inputSize];
            BitConverter.TryWriteBytes(input, control);

            if (!TryIoctl(report, stage, h, path, Ucsi.IOCTL_UCSI_SEND_COMMAND,
                          $"IOCTL_UCSI_SEND_COMMAND {name} control=0x{control:X16} in={inputSize}B",
                          input, 16, out _))
                continue;

            foreach (int outSize in new[] { 16, 256 })
            {
                if (TryIoctl(report, stage, h, path, Ucsi.IOCTL_UCSI_GET_MESSAGE,
                             $"IOCTL_UCSI_GET_MESSAGE after {name} out={outSize}B",
                             [], outSize, out byte[]? msg) && msg is { Length: > 0 })
                {
                    data = msg;
                    AcknowledgeCommandComplete(report, stage, h, path);
                    return true;
                }
            }
            AcknowledgeCommandComplete(report, stage, h, path);
        }
        return false;
    }

    /// <summary>ACK_CC_CI with the Command Completed Acknowledge bit, as UCSI requires.</summary>
    private static void AcknowledgeCommandComplete(Report report, string stage, SafeFileHandle h, string path)
    {
        byte[] input = new byte[8];
        BitConverter.TryWriteBytes(input, Ucsi.Control(0x04, 0, 0x02));
        TryIoctl(report, stage, h, path, Ucsi.IOCTL_UCSI_SEND_COMMAND,
                 "IOCTL_UCSI_SEND_COMMAND ACK_CC_CI (protocol handshake)", input, 16, out _);
    }

    private static unsafe bool TryIoctl(
        Report report, string stage, SafeFileHandle h, string path,
        uint code, string description, byte[] input, int outputSize, out byte[]? output)
    {
        output = null;
        byte[] inBuf = input.Length > 0 ? input : new byte[1];
        byte[] outBuf = new byte[Math.Max(outputSize, 1)];
        bool ok;
        uint returned;
        int err;

        fixed (byte* pIn = inBuf)
        fixed (byte* pOut = outBuf)
        {
            ok = Win32.DeviceIoControl(h, code, input.Length > 0 ? pIn : null, (uint)input.Length,
                                       outputSize > 0 ? pOut : null, (uint)outputSize,
                                       out returned, IntPtr.Zero);
            err = Marshal.GetLastWin32Error();
        }

        if (ok && returned > 0) output = outBuf[..(int)Math.Min(returned, (uint)outBuf.Length)];

        report.Add(new Attempt
        {
            Stage = stage,
            Api = "DeviceIoControl",
            Target = path,
            Detail = $"{description} code=0x{code:X8}",
            Success = ok,
            Win32Error = ok ? 0 : err,
            Error = ok ? null : Win32.Describe(err),
            Result = ok ? $"{returned} bytes returned" : null,
            DataHex = output is not null ? Convert.ToHexString(output) : null,
        });

        return ok;
    }
}
