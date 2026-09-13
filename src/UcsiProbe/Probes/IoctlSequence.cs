using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UcsiProbe.Native;

namespace UcsiProbe.Probes;

/// <summary>
/// Second-stage discovery of the in-box test interface calling convention.
///
/// The first sweep established two facts. Function 0x404 called with no input and no output
/// buffer returns success; everything else returns STATUS_INVALID_DEVICE_STATE (Win32 22). And
/// after that sweep the interface stopped opening at all, with ERROR_FILE_NOT_FOUND, even though
/// the device stayed healthy and the interface class stayed registered. That points at the
/// successful 0x404 call being a one-shot that tears the interface down rather than a handshake
/// that opens it.
///
/// So these experiments are ordered coldest first, and each one can restart the device before it
/// runs, which republishes the interface. That way a call that kills the interface cannot
/// invalidate the experiments that follow it. Payloads are zero-filled or carry a UCSI GET_*
/// opcode; no SET_*, reset, or firmware command is ever sent.
/// </summary>
internal static class IoctlSequence
{
    private static readonly uint F404 = Win32.CtlCode(Ucsi.FILE_DEVICE_UCSI, 0x404, 0, 0);
    private static readonly uint F406 = Win32.CtlCode(Ucsi.FILE_DEVICE_UCSI, 0x406, 0, 0);

    /// <summary>Restarts the UCSI device and returns the interface path again, or null.</summary>
    internal delegate string? DeviceReviver();

    internal static void Run(Report report, string stage, string path, DeviceReviver? revive)
    {
        // Coldest first: anything that might be destructive runs last.
        Experiment(report, stage, path, revive, "A: 0x406 cold, output sweep, no 0x404 first",
            (r, h, p) =>
            {
                foreach (int outSize in new[] { 0, 4, 16, 64, 256, 1024 })
                    Call(r, stage, h, p, F406, $"0x406 cold out={outSize}B", [], outSize);
            });

        Experiment(report, stage, path, revive, "B: 0x406 cold, input size sweep, out=1024",
            (r, h, p) =>
            {
                foreach (int size in InputSizes())
                    Call(r, stage, h, p, F406, $"0x406 cold in={size}B", new byte[size], 1024);
            });

        Experiment(report, stage, path, revive, "C: 0x406 cold carrying a UCSI CONTROL",
            (r, h, p) =>
            {
                foreach (int size in new[] { 8, 16, 24, 32, 48, 64 })
                {
                    byte[] buf = new byte[size];
                    BitConverter.TryWriteBytes(buf, Ucsi.Control(Ucsi.CMD_GET_CAPABILITY));
                    Call(r, stage, h, p, F406, $"0x406 CONTROL in={size}B out=1024", buf, 1024);
                }
            });

        Experiment(report, stage, path, revive, "D: 0x404 input size sweep, without the zero-buffer call",
            (r, h, p) =>
            {
                foreach (int size in InputSizes())
                    Call(r, stage, h, p, F404, $"0x404 in={size}B out=1024", new byte[size], 1024);
            });

        Experiment(report, stage, path, revive, "E: 0x404 carrying a UCSI CONTROL",
            (r, h, p) =>
            {
                foreach (int size in new[] { 8, 16, 24, 32, 48, 64 })
                {
                    byte[] buf = new byte[size];
                    BitConverter.TryWriteBytes(buf, Ucsi.Control(Ucsi.CMD_GET_CAPABILITY));
                    Call(r, stage, h, p, F404, $"0x404 CONTROL in={size}B out=1024", buf, 1024);
                }
            });

        // Last, because it is the call suspected of closing the interface. Verify that suspicion
        // directly: succeed, then immediately try to reopen on a fresh handle.
        Experiment(report, stage, path, revive, "F: is the zero-buffer 0x404 a one-shot teardown",
            (r, h, p) =>
            {
                Call(r, stage, h, p, F404, "0x404 zero-buffer call", [], 0);
                Call(r, stage, h, p, F406, "0x406 on same handle afterwards out=256", [], 256);
            },
            afterClose: (r, p) =>
            {
                SafeFileHandle again = Open(p, out int err);
                Console.WriteLine(again.IsInvalid
                    ? $"      reopen after the 0x404 call -> FAILED {Win32.Describe(err)}"
                    : "      reopen after the 0x404 call -> succeeded, so it is not a teardown");
                r.Add(new Attempt
                {
                    Stage = stage, Api = "CreateFileW", Target = p,
                    Detail = "reopen after zero-buffer 0x404",
                    Success = !again.IsInvalid, Win32Error = again.IsInvalid ? err : 0,
                    Error = again.IsInvalid ? Win32.Describe(err) : null,
                });
                again.Dispose();
            });
    }

    private static IEnumerable<int> InputSizes()
    {
        for (int i = 1; i <= 16; i++) yield return i;
        yield return 20; yield return 24; yield return 32; yield return 40;
        yield return 48; yield return 56; yield return 64; yield return 128;
    }

    private static SafeFileHandle Open(string path, out int err)
    {
        SafeFileHandle h = Win32.CreateFile(
            path, Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero,
            Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        err = Marshal.GetLastWin32Error();
        return h;
    }

    private static void Experiment(
        Report report, string stage, string path, DeviceReviver? revive, string title,
        Action<Report, SafeFileHandle, string> body,
        Action<Report, string>? afterClose = null)
    {
        Console.WriteLine($"  --- {title}");

        SafeFileHandle h = Open(path, out int err);
        string current = path;

        if (h.IsInvalid && revive is not null)
        {
            h.Dispose();
            Console.WriteLine($"      interface not openable ({Win32.Describe(err)}); restarting device");
            string? revived = revive();
            if (revived is null)
            {
                Console.WriteLine("      device restart did not republish the interface; skipping");
                return;
            }
            current = revived;
            h = Open(current, out err);
        }

        if (h.IsInvalid)
        {
            Console.WriteLine($"      open failed: {Win32.Describe(err)}");
            h.Dispose();
            return;
        }

        using (h) body(report, h, current);
        afterClose?.Invoke(report, current);
    }

    private static unsafe void Call(Report report, string stage, SafeFileHandle h, string path,
                                    uint code, string label, byte[] input, int outSize)
    {
        byte[] inBuf = input.Length > 0 ? input : new byte[1];
        byte[] outBuf = new byte[Math.Max(outSize, 1)];
        bool ok;
        uint returned;
        int err;

        fixed (byte* pIn = inBuf)
        fixed (byte* pOut = outBuf)
        {
            ok = Win32.DeviceIoControl(h, code,
                                       input.Length > 0 ? pIn : null, (uint)input.Length,
                                       outSize > 0 ? pOut : null, (uint)outSize,
                                       out returned, IntPtr.Zero);
            err = Marshal.GetLastWin32Error();
        }

        // Win32 22 is this interface's uninformative default "no". Everything else is signal.
        const int ErrorBadCommand = 22;
        if (!ok && err == ErrorBadCommand) return;

        byte[]? data = ok && returned > 0 ? outBuf[..(int)Math.Min(returned, (uint)outBuf.Length)] : null;
        string verdict = ok ? $"OK bytesReturned={returned}" : Win32.Describe(err);
        Console.WriteLine($"      {label,-42} -> {verdict}");
        if (data is not null) Console.WriteLine($"      {"",-42}    data {Convert.ToHexString(data)}");

        report.Add(new Attempt
        {
            Stage = stage, Api = "DeviceIoControl", Target = path,
            Detail = $"{label} code=0x{code:X8}",
            Success = ok, Win32Error = ok ? 0 : err,
            Error = ok ? null : Win32.Describe(err),
            Result = $"bytesReturned={returned}",
            DataHex = data is not null ? Convert.ToHexString(data) : null,
        });
    }
}
