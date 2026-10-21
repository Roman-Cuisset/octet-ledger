using System.Text;
using System.Text.Json;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public class BoundedCollectorLogTests
{
    [Fact]
    public void RepeatsRetainFirstLastAndCountWithoutConcealingCollectionHealth()
    {
        using var fixture = new JournalFixture();
        var journal = fixture.Create();
        var now = DateTimeOffset.Parse("2026-10-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        journal.RecordError(new IOException("same failure"), now);
        journal.RecordError(new IOException("same failure"), now.AddSeconds(1));
        journal.Record("launcher_error", now.AddSeconds(2), "LauncherError", "5");
        journal.RecordError(new IOException("same failure"), now.AddSeconds(3));
        journal.RecordError(new InvalidOperationException("different failure"), now.AddSeconds(4));

        Assert.Equal(4, journal.ReadConsecutiveErrors());
        journal.RecordSuccess(now.AddSeconds(5));
        Assert.Equal(0, journal.ReadConsecutiveErrors());
        journal.RecordSuccess(now.AddSeconds(6));
        journal.RecordError(new IOException("same failure"), now.AddSeconds(7));
        Assert.Equal(1, journal.ReadConsecutiveErrors());

        var events = journal.ReadEvents(now, now.AddMinutes(1));
        Assert.Equal(["error", "launcher_error", "error", "error", "recovery", "error"], events.Select(item => item.Kind));
        Assert.Equal([2L, 1L, 1L, 1L, 1L, 1L], events.Select(item => item.Count));
        Assert.Equal(now, events[0].TimestampUtc);
        Assert.Equal(now.AddSeconds(1), events[0].LastTimestampUtc);
        Assert.Equal("IOException", events[0].ErrorType);
        Assert.Equal("InvalidOperationException", events[3].ErrorType);
        Assert.Null(events[4].ErrorType);
    }

    [Fact]
    public async Task IndependentConcurrentWritersShareDeduplicationAndHealthState()
    {
        using var fixture = new JournalFixture();
        var now = DateTimeOffset.UtcNow;
        const int count = 64;
        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(() =>
            fixture.Create().RecordError(new IOException("identical"), now))));

        var journal = fixture.Create();
        var item = Assert.Single(journal.ReadEvents(now, now));
        Assert.Equal(count, item.Count);
        Assert.Equal(count, journal.ReadConsecutiveErrors());
        Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "collector.log*"));
        using var current = JsonDocument.Parse(File.ReadAllText(fixture.LogPath));
        Assert.Equal(count, current.RootElement.GetProperty("Count").GetInt64());
    }

    [Fact]
    public void RotationRetainsFixedFileCountAndBoundedRecords()
    {
        using var fixture = new JournalFixture();
        var journal = fixture.Create(BoundedCollectorLog.MaximumRecordBytes);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 200; index++)
            journal.Record("event", now.AddSeconds(index), message: new string('x', 256) + index);

        var files = Directory.GetFiles(fixture.DirectoryPath, "collector.log*");
        Assert.Equal(BoundedCollectorLog.RetainedFileCount + 1, files.Length);
        foreach (var file in files)
        {
            Assert.InRange(new FileInfo(file).Length, 1L, (long)BoundedCollectorLog.MaximumRecordBytes);
            foreach (var line in File.ReadLines(file))
            {
                Assert.True(Encoding.UTF8.GetByteCount(line) <= BoundedCollectorLog.MaximumRecordBytes);
                using var record = JsonDocument.Parse(line);
                Assert.Equal("event", record.RootElement.GetProperty("Kind").GetString());
            }
        }
        var events = journal.ReadEvents(now, now.AddHours(1));
        Assert.InRange(events.Count, 2, 199);
        Assert.Equal(now.AddSeconds(199), events[^1].TimestampUtc);
        Assert.DoesNotContain(events, item => item.TimestampUtc == now);
    }

    [Fact]
    public void OversizedLegacyLogsAreReplacedAndMessagesCannotCreateUnboundedOrInjectedRecords()
    {
        using var fixture = new JournalFixture();
        File.WriteAllText(fixture.LogPath, new string('x', BoundedCollectorLog.MaximumFileBytes * 2));
        File.WriteAllText(fixture.LogPath + ".previous", new string('x', BoundedCollectorLog.MaximumFileBytes * 2));
        var journal = fixture.Create();
        var now = DateTimeOffset.UtcNow;
        var message = string.Concat(Enumerable.Repeat("\n\0\"\\", 10_000));
        journal.RecordError(new IOException(message + "first"), now);
        journal.RecordError(new IOException(message + "second"), now.AddSeconds(1));

        Assert.False(File.Exists(fixture.LogPath + ".previous"));
        foreach (var file in Directory.GetFiles(fixture.DirectoryPath, "collector.log*"))
        {
            Assert.True(new FileInfo(file).Length <= BoundedCollectorLog.MaximumRecordBytes);
            Assert.Single(File.ReadLines(file));
        }
        var events = journal.ReadEvents(now, now.AddMinutes(1));
        Assert.Equal(2, events.Count); // Matching truncated prefixes are not identical full errors.
        Assert.All(events, item => Assert.Equal(1, item.Count));
        using var active = JsonDocument.Parse(File.ReadAllText(fixture.LogPath));
        Assert.True(active.RootElement.GetProperty("Message").GetString()!.Length <= 256);
    }

    [Fact]
    public void TimeWindowReturnsOverlappingGroupWithHonestFullSpan()
    {
        using var fixture = new JournalFixture();
        var journal = fixture.Create();
        var now = DateTimeOffset.UtcNow;
        journal.RecordError(new IOException("same"), now);
        journal.RecordError(new IOException("same"), now.AddMinutes(2));

        var item = Assert.Single(journal.ReadEvents(now.AddMinutes(1), now.AddMinutes(1)));
        Assert.Equal(2, item.Count);
        Assert.Equal(now, item.TimestampUtc);
        Assert.Equal(now.AddMinutes(2), item.LastTimestampUtc);
        Assert.Empty(journal.ReadEvents(now.AddMinutes(3), now.AddMinutes(4)));
        Assert.Throws<ArgumentException>(() => journal.ReadEvents(now, now.AddSeconds(-1)));
    }

    private sealed class JournalFixture : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"octetledger-journal-{Guid.NewGuid():N}");
        internal string LogPath => Path.Combine(DirectoryPath, "collector.log");

        internal JournalFixture() => Directory.CreateDirectory(DirectoryPath);
        internal BoundedCollectorLog Create(int maximumFileBytes = BoundedCollectorLog.MaximumFileBytes) =>
            new(LogPath, Path.Combine(DirectoryPath, "collector.errors"), maximumFileBytes);
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
