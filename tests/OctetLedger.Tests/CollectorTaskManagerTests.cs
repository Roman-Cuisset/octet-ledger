using OctetLedger.Core;

namespace OctetLedger.Tests;

public class CollectorTaskManagerTests
{
    [Fact]
    public void LauncherWaitsForCollectorAndRestartsAfterCrash()
    {
        var script = CollectorTaskManager.BuildLauncherScript(
            @"C:\Program Files\OctetLedger\octetledger.exe",
            @"C:\Data\collector.log");

        Assert.Contains("shell.Run(command, 0, True)", script);
        Assert.Contains("ElseIf exitCode <> 0 Then", script);
        Assert.Contains("restarting", script);
        Assert.Contains("WScript.Sleep 5000", script);
        Assert.Contains("C:\\Data\\collector.log", script);
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
}
