using System.ComponentModel;
using System.Diagnostics;

namespace Portmark.Core.Usb4;

/// <summary>What a tool run returned: its exit code, and its output or why it could not run.</summary>
public readonly record struct ToolResult(int ExitCode, string Output);

/// <summary>
/// The machine-facing steps of a capture, behind a seam so the order of operations and the cleanup
/// can be tested without administrator rights or a kernel trace session.
/// </summary>
public interface IUsb4CaptureHost
{
    /// <summary>The cross-process capture lock, or null when another capture holds it.</summary>
    IDisposable? TryAcquireCaptureLock();
    ToolResult Run(string tool, string arguments);
    void CreateDirectory(string dir);
    string? ReadFileIfExists(string path);

    /// <summary>True when the directory is gone afterwards.</summary>
    bool DeleteDirectory(string dir);
}

/// <summary>The outcome of one capture.</summary>
public sealed class Usb4Collection
{
    /// <summary>tracerpt's XML, or null with <see cref="Error"/> saying which step failed.</summary>
    public string? Xml { get; init; }
    public string? Error { get; init; }

    /// <summary>True when another portmark capture held the lock, so nothing was started or stopped.</summary>
    public bool AnotherCaptureRunning { get; init; }

    /// <summary>
    /// What could not be put back: a session that would not stop, a folder that would not delete.
    /// Each says what was left and how to remove it. Empty when nothing was left behind.
    /// </summary>
    public IReadOnlyList<string> CleanupProblems { get; init; } = [];
}

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
/// The session is stopped and the temporary files deleted on every path, including a failed step
/// and Ctrl+C. Stopping or deleting can itself fail, though (logman can hang, a file can stay
/// locked), so that is not a promise: whatever could not be put back is returned in
/// <see cref="Usb4Collection.CleanupProblems"/> for the command to say, with the session name and
/// the folder left behind. An earlier version ignored the result of the final stop and deleted
/// silently, so a session left running and holding its .etl went unmentioned.
/// </summary>
public static class Usb4Collector
{
    /// <summary>
    /// A fixed name, not a unique one. If portmark is ever killed outright, its session keeps
    /// running in the kernel after the process has gone; a fixed name lets the next run find and
    /// stop it before starting its own, so a crash cannot leave sessions accumulating.
    /// </summary>
    public const string SessionName = "portmark-usb4";

    /// <summary>
    /// Held for the whole capture and its cleanup. The fixed session name means a second elevated
    /// capture's stale-session recovery would stop the first capture's live session; the mutex
    /// makes the second one refuse instead. Global, so it spans terminal sessions too.
    /// </summary>
    public const string LockName = @"Global\portmark-usb4";

    public const string HostRouterProviderGuid = "{575BA31F-2B45-58C2-64FD-F5DC757B6137}";
    public const string DeviceRouterProviderGuid = "{AE795D36-2B11-5EFB-C7E0-5D552BC55D6C}";

    /// <summary>
    /// The events arrived within 0.1 seconds of each provider being enabled on the capture that
    /// was made. Three seconds leaves room for a larger domain without making the command slow.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// PLA_E_DCS_NOT_FOUND, "Data Collector Set was not found": what logman returns when stopping a
    /// session that does not exist, checked on Windows 11 26200. For a stop it means the same as
    /// success, as there is no session holding anything.
    /// </summary>
    private const int SessionNotFound = unchecked((int)0x80300002);

    public static Usb4Collection Collect(TimeSpan window, CancellationToken cancel) =>
        Collect(window, cancel, SystemCaptureHost.Instance,
                Path.Combine(Path.GetTempPath(), $"portmark-usb4-{Guid.NewGuid():N}"));

    public static Usb4Collection Collect(TimeSpan window, CancellationToken cancel, IUsb4CaptureHost host, string dir)
    {
        using IDisposable? held = host.TryAcquireCaptureLock();
        if (held is null)
            return new Usb4Collection
            {
                AnotherCaptureRunning = true,
                Error = "another portmark usb4 capture is already running on this PC. Wait for it to finish, then run this again.",
            };

        var problems = new List<string>();
        bool sessionMayExist = false;
        bool directoryCreated = false;
        string? xml = null;
        string? error;

        try
        {
            error = Capture();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            error = ex.Message;
        }
        finally
        {
            if (sessionMayExist && !StopSession(host, out string output))
                problems.Add($"The ETW trace session '{SessionName}' could not be stopped and may still be running ({output}). "
                           + $"Stop it from an elevated terminal with: logman stop {SessionName} -ets");

            if (directoryCreated && !host.DeleteDirectory(dir))
                problems.Add($"The temporary folder {dir} could not be deleted and was left in place. It holds only this "
                           + "capture's trace files; delete it once the trace session has stopped.");
        }

        return new Usb4Collection { Xml = error is null ? xml : null, Error = error, CleanupProblems = problems };

        string? Capture()
        {
            // Under the lock, any session with this name is left by a run that died: recover it. A
            // stop that fails for any reason other than "no such session" means one may still be
            // running, and starting another over it would fail or, worse, share its .etl.
            if (!StopSession(host, out string output))
                return $"a '{SessionName}' trace session left by an earlier run could not be stopped, so no new one was "
                     + $"started: {output} Try 'logman stop {SessionName} -ets' from an elevated terminal.";

            host.CreateDirectory(dir);
            directoryCreated = true;
            string etl = Path.Combine(dir, "usb4.etl");
            string xmlPath = Path.Combine(dir, "usb4.xml");

            // Set before starting, not after: a Ctrl+C can kill logman after it has created the
            // session but before its exit code reaches us.
            sessionMayExist = true;

            // Enabling a provider is what makes its driver emit the rundown: Microsoft documents
            // the events as reported "when a ETW trace session for the following trace providers
            // is enabled". Host router first, then the device router, which describes the ports.
            ToolResult run = host.Run("logman.exe", $"create trace {SessionName} -p {HostRouterProviderGuid} 0xFFFFFFFFFFFFFFFF 0xFF -o \"{etl}\" -ets");
            if (run.ExitCode != 0) return $"logman could not start the trace session: {run.Output}";

            run = host.Run("logman.exe", $"update trace {SessionName} -p {DeviceRouterProviderGuid} 0xFFFFFFFFFFFFFFFF 0xFF -ets");
            if (run.ExitCode != 0) return $"logman could not enable the USB4 device router provider: {run.Output}";

            cancel.WaitHandle.WaitOne(window);

            // The .etl is only complete once the session has flushed and closed it, so a stop that
            // did not succeed means there is nothing trustworthy to decode. The finally retries it.
            bool stopped = StopSession(host, out output);
            if (stopped) sessionMayExist = false;

            if (cancel.IsCancellationRequested) return "cancelled.";
            if (!stopped) return $"logman could not stop the trace session, so the trace was not decoded: {output}";

            run = host.Run("tracerpt.exe", $"\"{etl}\" -o \"{xmlPath}\" -of XML -y");
            if (run.ExitCode != 0 || host.ReadFileIfExists(xmlPath) is not { } text)
                return $"tracerpt could not decode the trace: {run.Output}";

            xml = text;
            return null;
        }
    }

    private static bool StopSession(IUsb4CaptureHost host, out string output)
    {
        ToolResult result = host.Run("logman.exe", $"stop {SessionName} -ets");
        output = result.Output;
        return result.ExitCode is 0 or SessionNotFound;
    }

    private sealed class SystemCaptureHost : IUsb4CaptureHost
    {
        public static readonly SystemCaptureHost Instance = new();

        private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(10);

        public IDisposable? TryAcquireCaptureLock()
        {
            Mutex mutex;
            try { mutex = new Mutex(initiallyOwned: false, LockName); }
            catch (UnauthorizedAccessException) { return null; }   // created by another account's capture

            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { acquired = true; }   // its holder died; its session is recovered next

            if (acquired) return new MutexRelease(mutex);
            mutex.Dispose();
            return null;
        }

        public ToolResult Run(string tool, string arguments)
        {
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
                if (process is null) return new ToolResult(-1, $"{tool} could not be started.");

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(ToolTimeout))
                {
                    // Waited for, not only killed: a logman still exiting can still be holding the
                    // session, and a tracerpt still exiting holds the .etl the cleanup is deleting.
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                    bool exited = process.WaitForExit(KillTimeout);
                    return new ToolResult(-1, exited
                        ? $"{tool} did not finish within {ToolTimeout.TotalSeconds:0} seconds and was stopped."
                        : $"{tool} did not finish within {ToolTimeout.TotalSeconds:0} seconds and could not be stopped.");
                }

                return new ToolResult(process.ExitCode, string.Join(" ", stdout.Result.Trim(), stderr.Result.Trim()).Trim());
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return new ToolResult(-1, ex.Message);
            }
        }

        public void CreateDirectory(string dir) => Directory.CreateDirectory(dir);

        public string? ReadFileIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

        public bool DeleteDirectory(string dir)
        {
            // tracerpt or the ETW session can hold the file for a moment after exiting.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }
            return !Directory.Exists(dir);
        }

        private sealed class MutexRelease(Mutex mutex) : IDisposable
        {
            public void Dispose()
            {
                // Released on the thread that took it: Collect is synchronous from lock to cleanup.
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }
}
