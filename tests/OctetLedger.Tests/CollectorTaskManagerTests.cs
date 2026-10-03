using OctetLedger.Core;

namespace OctetLedger.Tests;

public class CollectorTaskManagerTests
{

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
