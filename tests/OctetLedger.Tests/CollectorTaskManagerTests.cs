using OctetLedger.Core;

namespace OctetLedger.Tests;

public class CollectorTaskManagerTests
{

    [Fact]
    public void InternetMarkIsClearedOnlyForTheRunningOctetLedgerImage()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"octetledger-mark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "octetledger.exe");
        try
        {
            File.WriteAllText(path, "binary");
            File.WriteAllText($"{path}:Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
            File.WriteAllText($"{path}:unrelated", "keep");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

            Assert.True(CollectorTaskManager.TryClearInternetMark(path, path, "OctetLedger"));

            Assert.ThrowsAny<IOException>(() => File.ReadAllText($"{path}:Zone.Identifier"));
            Assert.Equal("keep", File.ReadAllText($"{path}:unrelated"));
            Assert.Equal("binary", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InternetMarkOfAnotherFileOrProductIsNeverCleared()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"octetledger-mark-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "octetledger.exe");
        var other = Path.Combine(directory, "other.exe");
        try
        {
            File.WriteAllText(path, "binary");
            File.WriteAllText(other, "other");
            File.WriteAllText($"{path}:Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");

            Assert.False(CollectorTaskManager.TryClearInternetMark(path, other, "OctetLedger"));
            Assert.False(CollectorTaskManager.TryClearInternetMark(path, path, "OtherProduct"));
            Assert.Contains("ZoneId=3", File.ReadAllText($"{path}:Zone.Identifier"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InternetMarkOfAPortableAliasClearsItsTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"octetledger-mark-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var packageDirectory = Directory.CreateDirectory(Path.Combine(directory, "package"));
        var target = Path.Combine(packageDirectory.FullName, "octetledger.exe");
        var alias = Path.Combine(directory, "octetledger.exe");
        try
        {
            File.WriteAllText(target, "binary");
            File.WriteAllText($"{target}:Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(-5));
            try { File.CreateSymbolicLink(alias, target); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { return; }

            Assert.True(CollectorTaskManager.TryClearInternetMark(alias, alias, "OctetLedger"));

            Assert.ThrowsAny<IOException>(() => File.ReadAllText($"{target}:Zone.Identifier"));
            Assert.Equal("binary", File.ReadAllText(alias));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InternetMarkOfAFileTouchedAfterProcessStartIsNotCleared()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"octetledger-mark-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "octetledger.exe");
        try
        {
            File.WriteAllText(path, "binary");
            File.WriteAllText($"{path}:Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
            // Simulates a file replaced after this process started.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

            Assert.False(CollectorTaskManager.TryClearInternetMark(path, path, "OctetLedger"));
            Assert.Contains("ZoneId=3", File.ReadAllText($"{path}:Zone.Identifier"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PortableAliasAndItsTargetAreTheSameExecutable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"octetledger-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "package.exe");
        var alias = Path.Combine(directory, "octetledger.exe");
        var other = Path.Combine(directory, "other.exe");
        try
        {
            File.WriteAllText(target, "binary");
            File.WriteAllText(other, "binary");
            try { File.CreateSymbolicLink(alias, target); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { return; }

            Assert.True(CollectorTaskManager.AreSameExecutablePath(alias, target));
            Assert.True(CollectorTaskManager.AreSameExecutablePath(alias, alias));
            Assert.False(CollectorTaskManager.AreSameExecutablePath(alias, other));
            Assert.False(CollectorTaskManager.AreSameExecutablePath(alias, ""));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
