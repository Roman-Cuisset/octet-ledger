using Microsoft.Data.Sqlite;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public class RestoreSafetyTests
{
    [Fact]
    public void ActiveCollectorOrDashboardConnectionRejectsRestoreWithoutChangingData()
    {
        using var fixture = new Fixture();
        Fixture.Seed(fixture.Destination, 100);
        Fixture.Seed(fixture.Source, 700);
        using var active = new TrafficStore(fixture.Destination);

        Assert.Throws<IOException>(() => TrafficStore.Restore(fixture.Source, fixture.Destination));

        Assert.Equal(100, Assert.Single(active.ReadTotalReportRows(Start.AddDays(1))).BytesReceived);
        Assert.Equal(700, Fixture.Total(fixture.Source));
        Assert.True(active.CheckIntegrity().IsHealthy);
    }

    [Fact]
    public void OrphanedDestinationWalIsRecoveredBeforeReplacingAndBackingUpTheDatabase()
    {
        using var fixture = new Fixture();
        Fixture.Seed(fixture.Source, 700);
        fixture.CopyUncheckpointedWal(includeMainFile: true);

        var result = TrafficStore.Restore(fixture.Source, fixture.Destination);

        Assert.Equal(700, Fixture.Total(fixture.Destination));
        Assert.NotNull(result.PreviousDatabaseBackupPath);
        Assert.Equal(400, Fixture.Total(result.PreviousDatabaseBackupPath));
        Assert.True(TrafficStore.CheckIntegrity(fixture.Destination).IsHealthy);
        Assert.True(TrafficStore.CheckIntegrity(result.PreviousDatabaseBackupPath).IsHealthy);
    }

    [Fact]
    public void MissingDestinationWithAnOrphanedWalIsRejectedWithoutChangingTheFiles()
    {
        using var fixture = new Fixture();
        Fixture.Seed(fixture.Source, 700);
        fixture.CopyUncheckpointedWal(includeMainFile: false);
        var journal = File.ReadAllBytes(fixture.Destination + "-wal");

        Assert.Throws<IOException>(() => TrafficStore.Restore(fixture.Source, fixture.Destination));

        Assert.False(File.Exists(fixture.Destination));
        Assert.Equal(journal, File.ReadAllBytes(fixture.Destination + "-wal"));
        Assert.Equal(700, Fixture.Total(fixture.Source));
    }

    [Fact]
    public void ActiveWalConnectionCannotBeDetachedFromItsDatabaseByRestore()
    {
        using var fixture = new Fixture();
        Fixture.Seed(fixture.Destination, 100);
        Fixture.Seed(fixture.Source, 700);
        using (var connection = new SqliteConnection($"Data Source={fixture.Destination};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; UPDATE traffic_minute SET bytes_received=400;";
            command.ExecuteNonQuery();

            Assert.Throws<IOException>(() => TrafficStore.Restore(fixture.Source, fixture.Destination));

            command.CommandText = "UPDATE traffic_minute SET bytes_received=500;";
            command.ExecuteNonQuery();
        }
        Assert.Equal(500, Fixture.Total(fixture.Destination));
        Assert.Equal(700, Fixture.Total(fixture.Source));
    }

    [Fact]
    public void CorruptExistingMainFileIsPreservedWhenReplacedByAValidBackup()
    {
        using var fixture = new Fixture();
        Fixture.Seed(fixture.Source, 700);
        const string corrupt = "damaged database retained for recovery";
        File.WriteAllText(fixture.Destination, corrupt);

        var result = TrafficStore.Restore(fixture.Source, fixture.Destination);

        Assert.NotNull(result.PreviousDatabaseBackupPath);
        Assert.Equal(corrupt, File.ReadAllText(result.PreviousDatabaseBackupPath));
        Assert.Equal(700, Fixture.Total(fixture.Destination));
    }

    [Fact]
    public void FailedReplacementPreservesDestinationAndCanBeRetriedAfterClosingTheReader()
    {
        using var fixture = new Fixture();
        Fixture.Seed(fixture.Source, 700);
        Fixture.Seed(fixture.Destination, 100);
        var original = File.ReadAllBytes(fixture.Destination);
        using (var reader = File.Open(fixture.Destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => TrafficStore.Restore(fixture.Source, fixture.Destination));
            Assert.Equal(original, File.ReadAllBytes(fixture.Destination));
        }

        var result = TrafficStore.Restore(fixture.Source, fixture.Destination);

        Assert.Equal(700, Fixture.Total(fixture.Destination));
        Assert.Equal(100, Fixture.Total(result.PreviousDatabaseBackupPath!));
    }

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"octetledger-restore-{Guid.NewGuid():N}");
        public string Source => Path.Combine(directory, "source.db");
        public string Destination => Path.Combine(directory, "active.db");
        public string Writer => Path.Combine(directory, "writer.db");

        public Fixture() => Directory.CreateDirectory(directory);

        public void CopyUncheckpointedWal(bool includeMainFile)
        {
            Seed(Writer, 100);
            using var writer = new SqliteConnection($"Data Source={Writer};Pooling=False");
            writer.Open();
            using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; UPDATE traffic_minute SET bytes_received=bytes_received+300;";
            command.ExecuteNonQuery();
            if (includeMainFile) File.Copy(Writer, Destination);
            File.Copy(Writer + "-wal", Destination + "-wal");
        }

        public static void Seed(string path, long received)
        {
            using var store = new TrafficStore(path);
            NetworkInterfaceSnapshot Snapshot(int minute, long bytes) => new(
                "wifi", "Wi-Fi", "Intel Wi-Fi", "Wireless80211", "Up", 1_000_000_000,
                bytes, 0, Start.AddMinutes(minute));
            store.Collect([Snapshot(0, 0)]);
            store.Collect([Snapshot(1, received)]);
        }

        public static long Total(string path)
        {
            using var store = new TrafficStore(path);
            return Assert.Single(store.ReadTotalReportRows(Start.AddDays(1))).BytesReceived;
        }

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
