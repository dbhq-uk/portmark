using Portmark.Core.Usb4;
using Xunit;

namespace Portmark.Core.Tests;

/// <summary>
/// The USB4 capture's order of operations and its cleanup, through a fake host. The real steps need
/// administrator rights and start a kernel trace session, so what is tested here is the part that
/// was got wrong once: decoding after a stop that failed, and saying nothing when the session or
/// its folder was left behind.
/// </summary>
public class Usb4CollectorTests
{
    private const string Dir = @"C:\Temp\portmark-usb4-test";
    private static readonly int NotFound = unchecked((int)0x80300002);

    private sealed class FakeHost : IUsb4CaptureHost
    {
        public bool LockHeldElsewhere;
        public bool LockReleased;
        public bool DirectoryDeletes = true;
        public string? XmlOnDisk = "<Events></Events>";
        public readonly List<string> Calls = [];
        public readonly Queue<int> StopResults = new();

        public IDisposable? TryAcquireCaptureLock()
        {
            Calls.Add("lock");
            return LockHeldElsewhere ? null : new Release(this);
        }

        public ToolResult Run(string tool, string arguments)
        {
            string verb = arguments.Split(' ')[0];
            Calls.Add($"{tool} {verb}");
            if (tool == "logman.exe" && verb == "stop")
                return new ToolResult(StopResults.Count > 0 ? StopResults.Dequeue() : 0, "stop output");
            return new ToolResult(0, $"{verb} output");
        }

        public void CreateDirectory(string dir) => Calls.Add("mkdir");
        public string? ReadFileIfExists(string path) => XmlOnDisk;

        public bool DeleteDirectory(string dir)
        {
            Calls.Add("rmdir");
            return DirectoryDeletes;
        }

        private sealed class Release(FakeHost host) : IDisposable
        {
            public void Dispose()
            {
                host.Calls.Add("unlock");
                host.LockReleased = true;
            }
        }
    }

    private static Usb4Collection Collect(FakeHost host, CancellationToken cancel = default) =>
        Usb4Collector.Collect(TimeSpan.Zero, cancel, host, Dir);

    [Fact]
    public void HappyPath_StopsBeforeDecoding_AndCleansUpUnderTheLock()
    {
        var host = new FakeHost();
        host.StopResults.Enqueue(NotFound);   // no stale session: the normal case

        Usb4Collection result = Collect(host);

        Assert.Equal("<Events></Events>", result.Xml);
        Assert.Null(result.Error);
        Assert.Empty(result.CleanupProblems);
        Assert.Equal(
            ["lock", "logman.exe stop", "mkdir", "logman.exe create", "logman.exe update", "logman.exe stop",
             "tracerpt.exe \"C:\\Temp\\portmark-usb4-test\\usb4.etl\"", "rmdir", "unlock"],
            host.Calls);
    }

    [Fact]
    public void AnotherCaptureHoldingTheLock_StopsNothingAndSaysSo()
    {
        // Without the lock, the stale-session recovery of one run would stop the other run's
        // session, because both use the same fixed name.
        var host = new FakeHost { LockHeldElsewhere = true };

        Usb4Collection result = Collect(host);

        Assert.True(result.AnotherCaptureRunning);
        Assert.Null(result.Xml);
        Assert.NotNull(result.Error);
        Assert.Equal(["lock"], host.Calls);
    }

    [Fact]
    public void AStaleSessionThatWillNotStop_IsNotReplaced()
    {
        var host = new FakeHost();
        host.StopResults.Enqueue(5);

        Usb4Collection result = Collect(host);

        Assert.Null(result.Xml);
        Assert.Contains(Usb4Collector.SessionName, result.Error);
        Assert.DoesNotContain("logman.exe create", host.Calls);
        Assert.True(host.LockReleased);
    }

    [Fact]
    public void AFailedStop_IsNeverDecoded_AndAnUnresolvedSessionAndFolderAreReported()
    {
        var host = new FakeHost { DirectoryDeletes = false };
        host.StopResults.Enqueue(NotFound);   // recovery
        host.StopResults.Enqueue(5);          // the stop after the window
        host.StopResults.Enqueue(5);          // the retry in cleanup

        Usb4Collection result = Collect(host);

        Assert.Null(result.Xml);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("tracerpt.exe", StringComparison.Ordinal));
        Assert.Equal(2, result.CleanupProblems.Count);
        Assert.Contains(result.CleanupProblems, p => p.Contains(Usb4Collector.SessionName) && p.Contains("logman stop"));
        Assert.Contains(result.CleanupProblems, p => p.Contains(Dir));
        Assert.True(host.LockReleased);
    }

    [Fact]
    public void AStopThatSucceedsOnRetry_StillIsNotDecoded_ButLeavesNothingToReport()
    {
        var host = new FakeHost();
        host.StopResults.Enqueue(NotFound);
        host.StopResults.Enqueue(5);
        host.StopResults.Enqueue(0);

        Usb4Collection result = Collect(host);

        Assert.Null(result.Xml);
        Assert.NotNull(result.Error);
        Assert.Empty(result.CleanupProblems);
    }

    [Fact]
    public void AFolderThatCannotBeDeleted_IsReported_ButTheCaptureStillStands()
    {
        var host = new FakeHost { DirectoryDeletes = false };

        Usb4Collection result = Collect(host);

        Assert.NotNull(result.Xml);
        Assert.Contains(Dir, Assert.Single(result.CleanupProblems));
    }

    [Fact]
    public void Cancelled_StopsTheSessionAndDecodesNothing()
    {
        var host = new FakeHost();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Usb4Collection result = Collect(host, cancel.Token);

        Assert.Null(result.Xml);
        Assert.Equal(2, host.Calls.Count(c => c == "logman.exe stop"));
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("tracerpt.exe", StringComparison.Ordinal));
        Assert.Empty(result.CleanupProblems);
    }
}
