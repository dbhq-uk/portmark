using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UcsiProbe.Native;

namespace UcsiProbe.Probes;

/// <summary>
/// Drives the UCSI PPM through the in-box test interface, using the calling convention recovered
/// by the discovery and sequence probes.
///
/// What was established empirically on this machine:
///   * Function 0x406 accepts a 48-byte buffer and returns 48 bytes. 48 is exactly the size of
///     the UCSI data structure: VERSION(2) + RESERVED(2) + CCI(4) + CONTROL(8) + MESSAGE_IN(16)
///     + MESSAGE_OUT(16). METHOD_BUFFERED shares one system buffer for input and output, so the
///     call is a read-modify-write: write CONTROL, read back CCI and MESSAGE_IN.
///   * Function 0x404 accepts a 1-byte buffer. Calling it poisons the handle: every later 0x406
///     on that handle returns STATUS_INVALID_DEVICE_STATE. It is not a required handshake and is
///     only used here as a fallback.
///   * A handle tolerates only a couple of 0x406 calls before going stale the same way, so each
///     UCSI command is issued on a freshly opened handle.
///
/// Only UCSI GET_* commands are issued, plus the ACK_CC_CI handshake the protocol requires after
/// a command completes. No SET_*, no PPM reset, no connector reset, no firmware command.
/// </summary>
internal static class UcsiSession
{
    internal const int BlockSize = 48;
    private const int OffVersion = 0;
    private const int OffCci = 4;
    private const int OffControl = 8;
    private const int OffMessageIn = 16;

    private static readonly uint F_DATA_BLOCK = Win32.CtlCode(Ucsi.FILE_DEVICE_UCSI, 0x406, 0, 0);

    private const uint CciBusy = 1u << 26;
    private const uint CciError = 1u << 28;
    private const uint CciCommandCompleted = 1u << 31;

    internal sealed record CommandResult(bool Ok, uint Cci, byte[] MessageIn, string? Error)
    {
        public bool Errored => (Cci & CciError) != 0;
        public int DataLength => (int)((Cci >> 8) & 0xFF);

        /// <summary>
        /// True when the PPM completed the command but returned no payload. That is a real answer
        /// meaning "nothing to report", and must never be decoded as though it were field data.
        /// </summary>
        public bool NoPayload => DataLength == 0 || MessageIn.All(b => b == 0);
    }

    internal static bool Run(Report report, string stage, string path)
    {
        byte[]? initial = WithFreshHandle<byte[]>(report, stage, path, "cold data block read",
                                                  h => ReadBlock(report, stage, h, path, "cold data block read")!);
        if (initial is null)
        {
            Console.WriteLine("  could not read the UCSI data block; stopping.");
            return false;
        }

        ushort version = BitConverter.ToUInt16(initial, OffVersion);
        uint cci = BitConverter.ToUInt32(initial, OffCci);
        Console.WriteLine($"  UCSI VERSION = 0x{version:X4}   CCI = 0x{cci:X8}");
        report.Notes.Add($"UCSI PPM version 0x{version:X4}, initial CCI 0x{cci:X8}");

        int connectors = ProbeCapability(report, stage, path);
        bool any = false;
        for (byte c = 1; c <= Math.Max(connectors, 1); c++)
            any |= ProbeConnector(report, stage, path, c);
        return any;
    }

    private static int ProbeCapability(Report report, string stage, string path)
    {
        CommandResult r = Execute(report, stage, path, Ucsi.CMD_GET_CAPABILITY,
                                  Ucsi.Control(Ucsi.CMD_GET_CAPABILITY));
        if (!r.Ok || r.MessageIn.Length < 5)
        {
            Console.WriteLine($"  GET_CAPABILITY -> {r.Error ?? "no data"}");
            return 0;
        }

        int connectors = r.MessageIn[4] & 0x7F;
        Console.WriteLine($"  GET_CAPABILITY -> {connectors} connector(s), attributes 0x{BitConverter.ToUInt32(r.MessageIn, 0):X8}");
        report.Notes.Add($"PPM reports {connectors} connector(s).");
        return connectors;
    }

    private static bool ProbeConnector(Report report, string stage, string path, byte connector)
    {
        Console.WriteLine($"  --- connector {connector}");

        CommandResult status = Execute(report, stage, path, Ucsi.CMD_GET_CONNECTOR_STATUS,
                                       Ucsi.ForConnector(Ucsi.CMD_GET_CONNECTOR_STATUS, connector));
        if (status.Ok && status.MessageIn.Length >= 7)
        {
            Console.WriteLine($"      GET_CONNECTOR_STATUS -> {Convert.ToHexString(status.MessageIn)}");
            Console.WriteLine($"      {ConnectorStatus.Describe(status.MessageIn)}");
            report.Notes.Add($"connector {connector} status: {ConnectorStatus.Describe(status.MessageIn)}");
        }
        else
        {
            Console.WriteLine($"      GET_CONNECTOR_STATUS -> {status.Error ?? "no data"}");
        }

        CommandResult cable = Execute(report, stage, path, Ucsi.CMD_GET_CABLE_PROPERTY,
                                      Ucsi.ForConnector(Ucsi.CMD_GET_CABLE_PROPERTY, connector));
        if (cable.Ok && cable.NoPayload)
        {
            Console.WriteLine("      GET_CABLE_PROPERTY -> command completed, no error, zero-length payload");

            // Distinguish "there is no e-marker to report" from "the PPM does not implement this
            // command". UCSI requires the PPM to raise the Error bit for an unrecognised command,
            // and GET_ERROR_STATUS then says why. Asking is cheaper than assuming.
            CommandResult err = Execute(report, stage, path, Ucsi.CMD_GET_ERROR_STATUS,
                                        Ucsi.Control(Ucsi.CMD_GET_ERROR_STATUS));
            string diagnosis = err.Ok && err.MessageIn.Length >= 2
                ? ErrorStatus.Describe(err.MessageIn)
                : "GET_ERROR_STATUS returned nothing";

            Console.WriteLine($"      GET_ERROR_STATUS -> {diagnosis}");
            Console.WriteLine("      reading: no e-marked cable on this connector");
            report.CablePropertyBytes.Add(
                $"connector {connector}: no cable data (completed, zero payload; error status: {diagnosis})");
            return false;
        }

        if (cable.Ok && cable.MessageIn.Length >= 5 && !cable.Errored)
        {
            string hex = Convert.ToHexString(cable.MessageIn.AsSpan(0, 5));
            Console.WriteLine($"      GET_CABLE_PROPERTY -> {hex}");
            Console.WriteLine($"      {CableProperty.Describe(cable.MessageIn)}");
            report.CablePropertyBytes.Add($"connector {connector}: {hex} :: {CableProperty.Describe(cable.MessageIn)}");
            return true;
        }

        Console.WriteLine($"      GET_CABLE_PROPERTY -> {(cable.Errored ? "PPM returned an error status" : cable.Error ?? "no data")}");
        return false;
    }

    /// <summary>
    /// Issues one UCSI command on its own handle: write CONTROL, wait for completion, read
    /// MESSAGE_IN, then acknowledge so the PPM can accept the next command.
    /// </summary>
    private static CommandResult Execute(Report report, string stage, string path,
                                         byte command, ulong control)
    {
        // The interface answers STATUS_INVALID_DEVICE_STATE if a command arrives while the PPM is
        // still settling from the previous one. A fresh process always works, a fresh handle does
        // not, so the state is in the driver rather than the handle: wait it out and retry.
        const int ErrorBadCommand = 22;
        CommandResult last = new(false, 0, [], "not attempted");

        for (int attempt = 0; attempt < 12; attempt++)
        {
            last = ExecuteOnce(report, stage, path, command, control, quiet: attempt > 0);
            if (last.Ok) return last;
            Thread.Sleep(150);
        }
        return last with { Error = $"{last.Error} (retried 12 times, Win32 {ErrorBadCommand} throughout)" };
    }

    private static CommandResult ExecuteOnce(Report report, string stage, string path,
                                             byte command, ulong control, bool quiet)
    {
        string name = Ucsi.CommandName(command);

        CommandResult? result = WithFreshHandle(report, stage, path, name, h =>
        {
            byte[] block = new byte[BlockSize];
            BitConverter.TryWriteBytes(block.AsSpan(OffControl), control);

            if (!Ioctl(report, stage, h, path, $"{name} control=0x{control:X16}", block, out byte[]? outBlock, quiet)
                || outBlock is null)
            {
                return new CommandResult(false, 0, [], "IOCTL rejected the command");
            }

            uint cci = BitConverter.ToUInt32(outBlock, OffCci);

            for (int i = 0; i < 50 && (cci & CciBusy) != 0 && (cci & CciCommandCompleted) == 0; i++)
            {
                Thread.Sleep(10);
                byte[]? polled = ReadBlock(report, stage, h, path, $"poll after {name}", quiet: true);
                if (polled is null) break;
                outBlock = polled;
                cci = BitConverter.ToUInt32(outBlock, OffCci);
            }

            int length = (int)((cci >> 8) & 0xFF);
            byte[] message = outBlock.AsSpan(OffMessageIn, 16).ToArray();
            if (length is > 0 and <= 16) message = message.AsSpan(0, length).ToArray();

            // ACK_CC_CI with the Command Completed Acknowledge bit. Skipping it leaves the PPM
            // waiting on the previous command.
            if ((cci & CciCommandCompleted) != 0)
            {
                byte[] ack = new byte[BlockSize];
                BitConverter.TryWriteBytes(ack.AsSpan(OffControl), Ucsi.Control(0x04, 0, 0x02));
                Ioctl(report, stage, h, path, $"ACK_CC_CI after {name}", ack, out _, quiet: true);
            }

            return new CommandResult(true, cci, message, null);
        });

        return result ?? new CommandResult(false, 0, [], "could not open the test interface");
    }

    /// <summary>
    /// Runs an operation on a newly opened handle. The interface tolerates only a small number of
    /// calls per handle, so every command gets a clean one.
    /// </summary>
    private static T? WithFreshHandle<T>(Report report, string stage, string path, string label,
                                         Func<SafeFileHandle, T> body) where T : class
    {
        SafeFileHandle h = Win32.CreateFile(
            path, Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero,
            Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();

        if (h.IsInvalid)
        {
            report.Add(new Attempt
            {
                Stage = stage, Api = "CreateFileW", Target = path, Detail = $"fresh handle for {label}",
                Success = false, Win32Error = err, Error = Win32.Describe(err),
            });
            h.Dispose();
            return null;
        }

        using (h) return body(h);
    }

    private static byte[]? ReadBlock(Report report, string stage, SafeFileHandle h, string path,
                                     string label, bool quiet = false)
    {
        byte[] block = new byte[BlockSize];
        return Ioctl(report, stage, h, path, label, block, out byte[]? outBlock, quiet) ? outBlock : null;
    }

    private static unsafe bool Ioctl(Report report, string stage, SafeFileHandle h, string path,
                                     string label, byte[] block, out byte[]? output, bool quiet = false)
    {
        output = null;
        byte[] buf = (byte[])block.Clone();
        bool ok;
        uint returned;
        int err;

        fixed (byte* p = buf)
        {
            ok = Win32.DeviceIoControl(h, F_DATA_BLOCK, p, (uint)buf.Length, p, (uint)buf.Length,
                                       out returned, IntPtr.Zero);
            err = Marshal.GetLastWin32Error();
        }

        if (ok) output = buf;

        if (ok || !quiet)
        {
            report.Add(new Attempt
            {
                Stage = stage, Api = "DeviceIoControl", Target = path,
                Detail = $"{label} code=0x{F_DATA_BLOCK:X8} block={buf.Length}B",
                Success = ok, Win32Error = ok ? 0 : err,
                Error = ok ? null : Win32.Describe(err),
                Result = ok ? $"bytesReturned={returned}" : null,
                DataHex = ok ? Convert.ToHexString(buf) : null,
            });
        }
        return ok;
    }
}
