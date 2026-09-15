using System.ComponentModel;
using System.Diagnostics;

namespace Portmark.Core.Usb4;

/// <summary>
/// Collects the USB4 drivers' rundown with the tools Windows ships: logman to run a short ETW
/// session into a temporary .etl, tracerpt to decode it to XML. Needs administrator rights.
///
/// Why not an in-process session with the Microsoft.Diagnostics.Tracing.TraceEvent package, which
/// was the first choice: portmark ships as one self-contained single file, and TraceEvent carries
/// per-architecture native DLLs that it locates on disk at run time, with a known single-file
/// packaging problem (duplicate bundle paths) that needs build workarounds. It would also decode
/// through a different path from the one this parser is tested against. The capture recipe below
/// is the one that produced the test fixture, tracerpt is what decoded these self-describing
/// events when Windows' own event reader (Get-WinEvent) could not, and so the XML the command
/// parses is the same shape as the XML the tests check. It adds no dependency.
///
/// Nothing on the machine is left changed: the session is always stopped and the temporary files
/// always deleted, including when a step fails or the user presses Ctrl+C.
/// </summary>
public static class Usb4Collector
{
    /// <summary>
    /// A fixed name, not a unique one. If portmark is ever killed outright, its session keeps
    /// running in the kernel after the process has gone; a fixed name lets the next run find and
    /// stop it before starting its own, so a crash cannot leave sessions accumulating.
    /// </summary>
    public const string SessionName = "portmark-usb4";

    public const string HostRouterProviderGuid = "{575BA31F-2B45-58C2-64FD-F5DC757B6137}";
    public const string DeviceRouterProviderGuid = "{AE795D36-2B11-5EFB-C7E0-5D552BC55D6C}";

    /// <summary>
    /// The events arrived within 0.1 seconds of each provider being enabled on the capture that
    /// was made. Three seconds leaves room for a larger domain without making the command slow.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Returns tracerpt's XML, or null with <paramref name="error"/> saying which step failed.</summary>
    public static string? CollectXml(TimeSpan window, CancellationToken cancel, out string? error)
    {
        error = null;
        string dir = Path.Combine(Path.GetTempPath(), $"portmark-usb4-{Guid.NewGuid():N}");
        string etl = Path.Combine(dir, "usb4.etl");
        string xml = Path.Combine(dir, "usb4.xml");
        bool sessionMayExist = false;

        try
        {
            Directory.CreateDirectory(dir);

            // A leftover session from a run that was killed. Failure is the normal case: there is none.
            Run("logman.exe", $"stop {SessionName} -ets", out _);

            // Set before starting, not after: a Ctrl+C can kill logman after it has created the
            // session but before its exit code reaches us.
            sessionMayExist = true;

            // Enabling a provider is what makes its driver emit the rundown: Microsoft documents
            // the events as reported "when a ETW trace session for the following trace providers
            // is enabled". Host router first, then the device router, which describes the ports.
            if (Run("logman.exe", $"create trace {SessionName} -p {HostRouterProviderGuid} 0xFFFFFFFFFFFFFFFF 0xFF -o \"{etl}\" -ets",
                    out string output) != 0)
            {
                error = $"logman could not start the trace session: {output}";
                return null;
            }

            if (Run("logman.exe", $"update trace {SessionName} -p {DeviceRouterProviderGuid} 0xFFFFFFFFFFFFFFFF 0xFF -ets",
                    out output) != 0)
            {
                error = $"logman could not enable the USB4 device router provider: {output}";
                return null;
            }

            cancel.WaitHandle.WaitOne(window);

            // Stopped here as well as in finally: the .etl is only complete once the session has
            // flushed and closed it.
            if (Run("logman.exe", $"stop {SessionName} -ets", out output) == 0)
                sessionMayExist = false;

            if (cancel.IsCancellationRequested)
            {
                error = "cancelled.";
                return null;
            }

            if (Run("tracerpt.exe", $"\"{etl}\" -o \"{xml}\" -of XML -y", out output) != 0 || !File.Exists(xml))
            {
                error = $"tracerpt could not decode the trace: {output}";
                return null;
            }

            return File.ReadAllText(xml);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            if (sessionMayExist) Run("logman.exe", $"stop {SessionName} -ets", out _);
            DeleteDirectory(dir);
        }
    }

    private static int Run(string tool, string arguments, out string output)
    {
        output = "";
        try
        {
            // The full System32 path, because this runs elevated and must not pick up a
            // logman.exe from the current directory or the user's PATH.
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool), arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? process = Process.Start(psi);
            if (process is null) return -1;

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ToolTimeout))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                output = $"{tool} did not finish within {ToolTimeout.TotalSeconds:0} seconds.";
                return -1;
            }

            output = string.Join(" ", stdout.Result.Trim(), stderr.Result.Trim()).Trim();
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            output = ex.Message;
            return -1;
        }
    }

    private static void DeleteDirectory(string dir)
    {
        // tracerpt or the ETW session can hold the file for a moment after exiting.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
