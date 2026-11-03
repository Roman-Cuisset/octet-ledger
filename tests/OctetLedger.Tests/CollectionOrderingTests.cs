using Microsoft.Data.Sqlite;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public class CollectionOrderingTests
{
    [Theory]
    [InlineData(1, 200, 300)]
    [InlineData(2, 250, 450)]
    [InlineData(0, 50, 50)]
    public void LateOrRepeatedFrameCannotReplaceCommittedCounters(int minute, long received, long sent)
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(0, 100, 100)]);
        store.Collect([Snapshot(2, 300, 500)]);

        var ignored = store.Collect([Snapshot(minute, received, sent)]);

        Assert.Equal(0, ignored.BytesReceived + ignored.BytesSent);
        Assert.Equal(Start.AddMinutes(2), store.GetStatus().LastCollectionUtc);
        Assert.Equal(2, store.ReadCollectionHistory(Start, Start.AddMinutes(2).AddSeconds(1)).Days.Sum(day => day.Observations));
        store.Collect([Snapshot(3, 400, 700)]);
        var total = Assert.Single(store.ReadTotalReportRows(Start.AddMinutes(4)));
        Assert.Equal(300, total.BytesReceived);
        Assert.Equal(600, total.BytesSent);
        Assert.Equal(180, store.ReadBuckets(Start).Sum(bucket => bucket.IntervalSeconds));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateMissingOrDownFrameCannotInterruptAnActiveAdapter(bool down)
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(0, 100, 100)]);
        store.Collect([Snapshot(2, 300, 300)]);

        store.Collect(down ? [Snapshot(1, 200, 200) with { Status = "Down" }] : [], Start.AddMinutes(1));

        Assert.Equal(Start.AddMinutes(2), store.GetStatus().LastCollectionUtc);
        var history = store.ReadCollectionHistory(Start, Start.AddMinutes(2).AddSeconds(1));
        Assert.DoesNotContain(history.Gaps, gap => gap.InterfaceId == "wifi" || gap.Reason == "no_active_interfaces");
        Assert.Equal(2, history.Days.Sum(day => day.Observations));
        store.Collect([Snapshot(3, 400, 400)]);
        Assert.Equal(600, Assert.Single(store.ReadTotalReportRows(Start.AddMinutes(4))).TotalBytes);
    }

    [Fact]
    public void LateActiveFrameCannotCloseARecordedDisconnection()
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(0, 100, 100)]);
        store.Collect([], Start.AddMinutes(2));

        store.Collect([Snapshot(1, 200, 200)]);

        var history = store.ReadCollectionHistory(Start, Start.AddMinutes(2).AddSeconds(1));
        var gap = Assert.Single(history.Gaps, gap => gap.InterfaceId == "wifi");
        Assert.Equal("interface_not_observed", gap.Reason);
        Assert.Null(gap.EndUtc);
        Assert.Equal(Start.AddMinutes(2), history.LastSuccessUtc);
        store.Collect([Snapshot(3, 400, 400)]);
        var resumedGap = Assert.Single(store.ReadCollectionHistory(Start, Start.AddMinutes(3)).Gaps,
            gap => gap.InterfaceId == "wifi");
        Assert.Equal(Start.AddMinutes(3), resumedGap.EndUtc);
        Assert.Equal(600, Assert.Single(store.ReadTotalReportRows(Start.AddMinutes(4))).TotalBytes);
    }

    [Fact]
    public void LatePreResetFrameCannotReplaceTheResetBaseline()
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(0, 5_000, 5_000)]);
        store.Collect([Snapshot(2, 100, 200)]);
        store.Collect([Snapshot(1, 4_000, 4_000)]);

        store.Collect([Snapshot(3, 150, 300)]);

        var total = Assert.Single(store.ReadTotalReportRows(Start.AddMinutes(4)));
        Assert.Equal(50, total.BytesReceived);
        Assert.Equal(100, total.BytesSent);
        var gaps = store.ReadCollectionHistory(Start, Start.AddMinutes(3)).Gaps;
        Assert.Single(gaps, gap => gap.Reason == "counter_reset");
        Assert.DoesNotContain(gaps, gap => gap.Reason == "clock_not_advanced");
    }

    [Fact]
    public void MigratedBaselineWithoutCollectionHistoryRejectsOlderFrames()
    {
        using var database = new TestDatabase();
        using (var original = new TrafficStore(database.Path)) original.Collect([Snapshot(2, 300, 500)]);
        using (var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM collection_sample; DELETE FROM collection_state;";
            command.ExecuteNonQuery();
        }
        using var store = new TrafficStore(database.Path);

        store.Collect([Snapshot(1, 200, 300)]);

        Assert.Equal(Start.AddMinutes(2), store.GetStatus().LastCollectionUtc);
        store.Collect([Snapshot(3, 400, 700)]);
        var total = Assert.Single(store.ReadTotalReportRows(Start.AddMinutes(4)));
        Assert.Equal(100, total.BytesReceived);
        Assert.Equal(200, total.BytesSent);
        Assert.Equal(TrafficStore.CurrentSchemaVersion, store.ReadSchemaVersion());
    }

    [Fact]
    public async Task ConcurrentCurrentCapturesConserveCountersAcrossIndependentConnections()
    {
        using var database = new TestDatabase();
        using var first = new TrafficStore(database.Path);
        using var second = new TrafficStore(database.Path);
        first.Collect([Snapshot(0, 100, 200)]);
        using var start = new ManualResetEventSlim();
        var sequence = 0;
        var activeCaptures = 0;
        const int capturesPerWriter = 20;

        void CollectFrames(TrafficStore store)
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(10)));
            for (var index = 0; index < capturesPerWriter; index++)
            {
                store.CollectCurrent(() =>
                {
                    Assert.Equal(1, Interlocked.Increment(ref activeCaptures));
                    try
                    {
                        var minute = Interlocked.Increment(ref sequence);
                        return [Snapshot(minute, 100 + minute * 100, 200 + minute * 200)];
                    }
                    finally { Interlocked.Decrement(ref activeCaptures); }
                });
            }
        }

        var writers = new[] { Task.Run(() => CollectFrames(first)), Task.Run(() => CollectFrames(second)) };
        start.Set();
        await Task.WhenAll(writers);

        var total = Assert.Single(first.ReadTotalReportRows(Start.AddHours(1)));
        Assert.Equal(capturesPerWriter * 2 * 100, total.BytesReceived);
        Assert.Equal(capturesPerWriter * 2 * 200, total.BytesSent);
        Assert.Equal(Start.AddMinutes(capturesPerWriter * 2), first.GetStatus().LastCollectionUtc);
        var history = first.ReadCollectionHistory(Start, Start.AddMinutes(capturesPerWriter * 2).AddSeconds(1));
        Assert.Equal(capturesPerWriter * 2 + 1, history.Days.Sum(day => day.Observations));
        Assert.DoesNotContain(history.Gaps, gap => gap.Reason == "clock_not_advanced" || gap.Reason == "counter_reset");
    }

    [Fact]
    public void FailedCurrentCaptureReleasesTheWriterAndPreservesTheBaseline()
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(0, 100, 100)]);

        Assert.Throws<InvalidOperationException>(() => store.CollectCurrent(() => throw new InvalidOperationException("Capture failed.")));

        using var resumed = new TrafficStore(database.Path);
        var result = resumed.CollectCurrent(() => [Snapshot(1, 200, 300)]);
        Assert.Equal(100, result.BytesReceived);
        Assert.Equal(200, result.BytesSent);
        Assert.Equal(2, resumed.ReadCollectionHistory(Start, Start.AddMinutes(1).AddSeconds(1)).Days.Sum(day => day.Observations));
    }

    private static readonly DateTimeOffset Start = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);

    private static NetworkInterfaceSnapshot Snapshot(int minute, long received, long sent) => new(
        "wifi", "Wi-Fi", "Physical Wi-Fi", "Wireless80211", "Up", 1_000_000_000,
        received, sent, Start.AddMinutes(minute));

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"octetledger-ordering-{Guid.NewGuid():N}");
        public string Path => System.IO.Path.Combine(directory, "test.db");
        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
