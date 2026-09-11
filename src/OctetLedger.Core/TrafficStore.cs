using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OctetLedger.Core;

public sealed class TrafficStore : IDisposable
{
    private readonly SqliteConnection connection;

    public TrafficStore(string? databasePath = null)
    {
        DatabasePath = databasePath ?? AppDataPaths.DatabasePath;
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        InitializeSchema();
    }

    public string DatabasePath { get; }

    public CollectionResult Collect(IEnumerable<NetworkInterfaceSnapshot> snapshots)
    {
        var observed = snapshots
            .Where(snapshot => snapshot.Status == "Up" && snapshot.Type != "Loopback")
            .ToArray();
        var receivedTotal = 0L;
        var sentTotal = 0L;
        var baselines = 0;

        using var transaction = connection.BeginTransaction();
        foreach (var snapshot in observed)
        {
            var previous = ReadState(snapshot.Id, transaction);
            if (previous is null)
            {
                baselines++;
            }
            else
            {
                var receivedDelta = Math.Max(0, snapshot.BytesReceived - previous.Value.Received);
                var sentDelta = Math.Max(0, snapshot.BytesSent - previous.Value.Sent);
                receivedTotal += receivedDelta;
                sentTotal += sentDelta;

                if (receivedDelta > 0 || sentDelta > 0)
                {
                    AddToMinute(snapshot, receivedDelta, sentDelta, transaction);
                }
            }

            WriteState(snapshot, transaction);
        }

        transaction.Commit();
        return new CollectionResult(observed.Length, baselines, receivedTotal, sentTotal);
    }

    public IReadOnlyList<TrafficBucket> ReadBuckets(DateTimeOffset fromUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.interface_id, s.name, b.minute_utc, b.bytes_received, b.bytes_sent
            FROM traffic_minute b
            JOIN adapter_state s ON s.interface_id = b.interface_id
            WHERE b.minute_utc >= $fromUtc
            ORDER BY b.minute_utc DESC, s.name;
            """;
        command.Parameters.AddWithValue("$fromUtc", FormatTimestamp(fromUtc));

        using var reader = command.ExecuteReader();
        var buckets = new List<TrafficBucket>();
        while (reader.Read())
        {
            buckets.Add(new TrafficBucket(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return buckets;
    }

    public TrafficStoreStatus GetStatus()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM adapter_state),
                (SELECT COUNT(*) FROM traffic_minute),
                (SELECT MAX(last_seen_utc) FROM adapter_state);
            """;

        using var reader = command.ExecuteReader();
        reader.Read();
        DateTimeOffset? lastCollection = reader.IsDBNull(2)
            ? null
            : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var databaseBytes = File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0;

        return new TrafficStoreStatus(
            DatabasePath,
            databaseBytes,
            reader.GetInt32(0),
            reader.GetInt64(1),
            lastCollection);
    }

    public DatabaseCheckResult CheckIntegrity()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        var messages = new List<string>();
        while (reader.Read())
        {
            messages.Add(reader.GetString(0));
        }

        return new DatabaseCheckResult(
            messages.Count == 1 && string.Equals(messages[0], "ok", StringComparison.OrdinalIgnoreCase),
            messages);
    }

    public static DatabaseCheckResult CheckIntegrity(string databasePath)
    {
        try
        {
            using var readOnlyConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            readOnlyConnection.Open();
            using var command = readOnlyConnection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            using var reader = command.ExecuteReader();
            var messages = new List<string>();
            while (reader.Read()) messages.Add(reader.GetString(0));
            return new DatabaseCheckResult(
                messages.Count == 1 && string.Equals(messages[0], "ok", StringComparison.OrdinalIgnoreCase),
                messages);
        }
        catch (SqliteException exception)
        {
            return new DatabaseCheckResult(false, [$"SQLite error {exception.SqliteErrorCode}: {exception.Message}"]);
        }
    }

    public string Backup(string destinationPath)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        if (string.Equals(fullPath, Path.GetFullPath(DatabasePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Backup destination must differ from the active database.", nameof(destinationPath));
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        destination.Open();
        connection.BackupDatabase(destination);
        return fullPath;
    }

    public void Dispose()
    {
        connection.Dispose();
    }

    private void InitializeSchema()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = DELETE;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS adapter_state (
                interface_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                type TEXT NOT NULL,
                last_received INTEGER NOT NULL,
                last_sent INTEGER NOT NULL,
                last_seen_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS traffic_minute (
                interface_id TEXT NOT NULL,
                minute_utc TEXT NOT NULL,
                bytes_received INTEGER NOT NULL,
                bytes_sent INTEGER NOT NULL,
                PRIMARY KEY (interface_id, minute_utc),
                FOREIGN KEY (interface_id) REFERENCES adapter_state(interface_id)
            );

            CREATE INDEX IF NOT EXISTS ix_traffic_minute_time
                ON traffic_minute(minute_utc);
            """;
        command.ExecuteNonQuery();
    }

    private (long Received, long Sent)? ReadState(string interfaceId, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT last_received, last_sent
            FROM adapter_state
            WHERE interface_id = $interfaceId;
            """;
        command.Parameters.AddWithValue("$interfaceId", interfaceId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    private void WriteState(NetworkInterfaceSnapshot snapshot, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO adapter_state (
                interface_id, name, description, type, last_received, last_sent, last_seen_utc)
            VALUES ($id, $name, $description, $type, $received, $sent, $seen)
            ON CONFLICT(interface_id) DO UPDATE SET
                name = excluded.name,
                description = excluded.description,
                type = excluded.type,
                last_received = excluded.last_received,
                last_sent = excluded.last_sent,
                last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id);
        command.Parameters.AddWithValue("$name", snapshot.Name);
        command.Parameters.AddWithValue("$description", snapshot.Description);
        command.Parameters.AddWithValue("$type", snapshot.Type);
        command.Parameters.AddWithValue("$received", snapshot.BytesReceived);
        command.Parameters.AddWithValue("$sent", snapshot.BytesSent);
        command.Parameters.AddWithValue("$seen", FormatTimestamp(snapshot.CapturedAt));
        command.ExecuteNonQuery();
    }

    private void AddToMinute(
        NetworkInterfaceSnapshot snapshot,
        long received,
        long sent,
        SqliteTransaction transaction)
    {
        var utc = snapshot.CapturedAt.UtcDateTime;
        var minute = new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO traffic_minute (interface_id, minute_utc, bytes_received, bytes_sent)
            VALUES ($id, $minute, $received, $sent)
            ON CONFLICT(interface_id, minute_utc) DO UPDATE SET
                bytes_received = bytes_received + excluded.bytes_received,
                bytes_sent = bytes_sent + excluded.bytes_sent;
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id);
        command.Parameters.AddWithValue("$minute", FormatTimestamp(minute));
        command.Parameters.AddWithValue("$received", received);
        command.Parameters.AddWithValue("$sent", sent);
        command.ExecuteNonQuery();
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}

public sealed record DatabaseCheckResult(bool IsHealthy, IReadOnlyList<string> Messages);
