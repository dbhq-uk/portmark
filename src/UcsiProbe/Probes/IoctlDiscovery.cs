using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UcsiProbe.Native;

namespace UcsiProbe.Probes;

/// <summary>
/// Works out the calling convention of the in-box UCSI test interface.
///
/// The WDK sample publishes IOCTL functions 0x401/0x402/0x403. The shipping UcmUcsiCx.sys does
/// not: a scan of its code and rdata for constants with device type 4627 (FILE_DEVICE_UCSI)
/// finds exactly two, functions 0x404 and 0x406. Nothing public documents their buffer layouts,
/// so this probe discovers them by observation rather than by guessing blindly.
///
/// The method is deliberately non-destructive. It never sends a UCSI SET_* or reset command. It
/// leans on a property of METHOD_BUFFERED drivers: calling with an undersized output buffer makes
/// the driver report the size it wants via ERROR_INSUFFICIENT_BUFFER without performing the
/// operation, and calling with a wrong-sized input buffer is rejected before any hardware is
/// touched. Those two error codes alone reveal the expected shapes.
/// </summary>
internal static class IoctlDiscovery
{
    internal sealed record Shape(string Name, uint Code);

    internal static readonly Shape[] Candidates =
    [
        new("in-box function 0x404", Win32.CtlCode(Ucsi.FILE_DEVICE_UCSI, 0x404, 0, 0)),
        new("in-box function 0x406", Win32.CtlCode(Ucsi.FILE_DEVICE_UCSI, 0x406, 0, 0)),
        new("WDK sample SEND_COMMAND 0x401", Ucsi.IOCTL_UCSI_SEND_COMMAND),
        new("WDK sample GET_CCI 0x402", Ucsi.IOCTL_UCSI_GET_CCI),
        new("WDK sample GET_MESSAGE 0x403", Ucsi.IOCTL_UCSI_GET_MESSAGE),
    ];

    /// <summary>Input buffers to try, all of them inert as far as the PPM is concerned.</summary>
    private static IEnumerable<(string Label, byte[] Data)> InputShapes()
    {
        yield return ("empty", []);
        yield return ("4B zero", new byte[4]);
        yield return ("8B zero", new byte[8]);

        // A bare UCSI CONTROL carrying GET_CAPABILITY, which reads and changes nothing.
        byte[] control = new byte[8];
        BitConverter.TryWriteBytes(control, Ucsi.Control(Ucsi.CMD_GET_CAPABILITY));
        yield return ("8B CONTROL GET_CAPABILITY", control);

        // The UCSI data structure places CONTROL at offset 8 and MESSAGE_IN at offset 16, so a
        // data-block style IOCTL plausibly takes {offset, length, payload}.
        byte[] block = new byte[16];
        BitConverter.TryWriteBytes(block.AsSpan(0), 8u);
        BitConverter.TryWriteBytes(block.AsSpan(4), 8u);
        control.CopyTo(block.AsSpan(8));
        yield return ("16B {offset=8,len=8,CONTROL}", block);

        yield return ("16B zero", new byte[16]);
        yield return ("32B zero", new byte[32]);
    }

    private static readonly int[] OutputSizes = [0, 4, 16, 64, 256, 1024];

    internal static void Run(Report report, string stage, string interfacePath)
    {
        SafeFileHandle h = Win32.CreateFile(
            interfacePath, Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero,
            Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        int openErr = Marshal.GetLastWin32Error();

        if (h.IsInvalid)
        {
            report.Add(new Attempt
            {
                Stage = stage, Api = "CreateFileW", Target = interfacePath,
                Detail = "IOCTL shape discovery", Success = false,
                Win32Error = openErr, Error = Win32.Describe(openErr),
            });
            h.Dispose();
            return;
        }

        using (h)
        {
            foreach (Shape shape in Candidates)
            {
                foreach ((string inLabel, byte[] input) in InputShapes())
                {
                    foreach (int outSize in OutputSizes)
                    {
                        Probe(report, stage, h, interfacePath, shape, inLabel, input, outSize);
                    }
                }
            }
        }
    }

    private static unsafe void Probe(
        Report report, string stage, SafeFileHandle h, string path,
        Shape shape, string inLabel, byte[] input, int outSize)
    {
        byte[] inBuf = input.Length > 0 ? input : new byte[1];
        byte[] outBuf = new byte[Math.Max(outSize, 1)];
        bool ok;
        uint returned;
        int err;

        fixed (byte* pIn = inBuf)
        fixed (byte* pOut = outBuf)
        {
            ok = Win32.DeviceIoControl(h, shape.Code,
                                       input.Length > 0 ? pIn : null, (uint)input.Length,
                                       outSize > 0 ? pOut : null, (uint)outSize,
                                       out returned, IntPtr.Zero);
            err = Marshal.GetLastWin32Error();
        }

        // ERROR_NOT_SUPPORTED on every shape means the function does not exist on this driver.
        // Anything else is signal: record it. Suppress the pure noise case to keep the log usable.
        bool interesting = ok || err is not (Win32.ERROR_NOT_SUPPORTED or Win32.ERROR_INVALID_FUNCTION);
        if (!interesting) return;

        byte[]? data = ok && returned > 0
            ? outBuf[..(int)Math.Min(returned, (uint)outBuf.Length)]
            : null;

        report.Add(new Attempt
        {
            Stage = stage,
            Api = "DeviceIoControl",
            Target = path,
            Detail = $"{shape.Name} code=0x{shape.Code:X8} in={inLabel} outBuf={outSize}B",
            Success = ok,
            Win32Error = ok ? 0 : err,
            Error = ok ? null : Win32.Describe(err),
            Result = $"bytesReturned={returned}",
            DataHex = data is not null ? Convert.ToHexString(data) : null,
        });
    }
}
