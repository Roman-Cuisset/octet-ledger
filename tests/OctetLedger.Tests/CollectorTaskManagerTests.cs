using System.Diagnostics;
using System.Globalization;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public class CollectorTaskManagerTests
{

    [WindowsFact]
    public void StartupDiagnosticPreservesTheDownloadMarkOnTheAliasTarget()
    {
        var directory = Directory.CreateTempSubdirectory("octetledger-mark-");
        try
        {
            var target = Path.Combine(directory.FullName, "octetledger.exe");
            var alias = Path.Combine(directory.FullName, "alias.exe");
            var logPath = Path.Combine(directory.FullName, "collector.log");
            var zonePath = $"{target}:Zone.Identifier";
            const string zoneMark = "[ZoneTransfer]\r\nZoneId=3";
            File.WriteAllText(target, "binary");
            File.CreateSymbolicLink(alias, target);

            var unmarked = CollectorTaskManager.DescribeStartupFailure(logPath, alias);
            Assert.Contains(logPath, unmarked);
            Assert.DoesNotContain("SHA-256", unmarked);

            File.WriteAllText(zonePath, zoneMark);
            File.WriteAllText($"{target}:unrelated", "keep");

            var marked = CollectorTaskManager.DescribeStartupFailure(logPath, alias);
            Assert.Contains(target, marked);
            Assert.Contains("SHA-256", marked);
            Assert.Equal(zoneMark, File.ReadAllText(zonePath));
            Assert.Equal("keep", File.ReadAllText($"{target}:unrelated"));
            Assert.Equal("binary", File.ReadAllText(alias));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [WindowsFact]
    public void CollectorIdentityMatchesALiveProcessThroughAPortableAlias()
    {
        var directory = Directory.CreateTempSubdirectory("octetledger-identity-");
        try
        {
            var package = directory.CreateSubdirectory("package");
            var target = Path.Combine(package.FullName, "octetledger.exe");
            var alias = Path.Combine(directory.FullName, "octetledger.exe");
            var other = Path.Combine(directory.CreateSubdirectory("other").FullName, "octetledger.exe");
            // A copy of cmd.exe stays alive on redirected input without running collector or user startup code.
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), target);
            File.Copy(target, other);
            File.CreateSymbolicLink(alias, target);
            using var process = Process.Start(new ProcessStartInfo(target, "/d /q")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            Assert.NotNull(process);

            try
            {
                var pid = process.Id.ToString(CultureInfo.InvariantCulture);
                var start = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                Assert.True(CollectorTaskManager.IsExpectedCollectorProcess(process, [pid, start, target], alias));
                Assert.True(CollectorTaskManager.IsExpectedCollectorProcess(process, [pid, start, alias], target));
                Assert.False(CollectorTaskManager.IsExpectedCollectorProcess(process, [pid, "0", target], alias));
                Assert.False(CollectorTaskManager.IsExpectedCollectorProcess(process, [pid, start, other], alias));
                Assert.False(CollectorTaskManager.IsExpectedCollectorProcess(process, [pid, start, target], other));
            }
            finally
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(5000))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows process or file stream semantics.";
        }
    }

    [Fact]
    public async Task ProcessLockCanBeReleasedFromAnotherThread()
    {
        var name = $@"Local\OctetLedger.Tests.{Guid.NewGuid():N}";
        var processLock = CollectorProcessLock.TryAcquire(name);

        Assert.NotNull(processLock);
        Assert.Null(CollectorProcessLock.TryAcquire(name));
        await Task.Run(processLock.Dispose);

        using var reacquired = CollectorProcessLock.TryAcquire(name);
        Assert.NotNull(reacquired);
    }

    [Fact]
    public void WaitForStartupKeepsSlowCollectorPending()
    {
        var state = CollectorTaskManager.WaitForStartup(
            () => (Running: true, Ready: false),
            wait: () => { },
            attempts: 3);

        Assert.Equal(CollectorStartupState.Pending, state);
    }

    [Fact]
    public void WaitForStartupReportsReadyCollector()
    {
        var observations = new Queue<(bool Running, bool Ready)>(
        [
            (false, false),
            (true, false),
            (true, true)
        ]);

        var state = CollectorTaskManager.WaitForStartup(
            () => observations.Dequeue(),
            wait: () => { },
            attempts: 3);

        Assert.Equal(CollectorStartupState.Ready, state);
    }

    [Fact]
    public void WaitForStartupRejectsMissingProcess()
    {
        var state = CollectorTaskManager.WaitForStartup(
            () => (Running: false, Ready: false),
            wait: () => { },
            attempts: 3);

        Assert.Equal(CollectorStartupState.Failed, state);
    }

    [Fact]
    public void StatusReportsDelayedCollectionEvenWhenProcessStillExists()
    {
        var now = DateTimeOffset.UtcNow;

        var status = CollectorTaskManager.DetermineStatus(
            registered: true, launcherExists: true, running: true,
            lastSuccess: now.AddMinutes(-4), now);

        Assert.Equal("Running, collection delayed", status.State);
    }

    [Fact]
    public void StatusReportsRecentSuccessfulCollectionAsRunning()
    {
        var now = DateTimeOffset.UtcNow;

        var status = CollectorTaskManager.DetermineStatus(
            registered: true, launcherExists: true, running: true,
            lastSuccess: now.AddMinutes(-1), now);

        Assert.Equal("Running", status.State);
        Assert.Null(status.Details);
    }

    [Fact]
    public void WatchdogRestartsStalledCollectorButGivesNewProcessTimeToStart()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(CollectorTaskManager.ShouldRestartCollector(now.AddMinutes(-4), now.AddMinutes(-5), now));
        Assert.True(CollectorTaskManager.ShouldRestartCollector(null, now.AddMinutes(-4), now));
        Assert.False(CollectorTaskManager.ShouldRestartCollector(now.AddHours(-1), now.AddSeconds(-30), now));
        Assert.False(CollectorTaskManager.ShouldRestartCollector(now.AddMinutes(-3), now.AddMinutes(-5), now));
        Assert.False(CollectorTaskManager.ShouldRestartCollector(now.AddSeconds(-10), now.AddMinutes(-5), now));
    }

    [Fact]
    public void StatusReportsRepeatedErrorsBeforeCollectionBecomesLate()
    {
        var now = DateTimeOffset.UtcNow;

        var status = CollectorTaskManager.DetermineStatus(
            registered: true, launcherExists: true, running: true,
            lastSuccess: now.AddMinutes(-1), now, consecutiveErrors: 2);

        Assert.Equal("Running, retrying after errors", status.State);
    }
}
