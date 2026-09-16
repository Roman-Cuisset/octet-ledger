using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OctetLedger.Core;

public sealed class TrafficStore : IDisposable
{
    public const int CurrentSchemaVersion = TrafficDatabaseSchema.CurrentVersion;
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
        try
        {
            connection.Open();
            TrafficDatabaseSchema.Initialize(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
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
            else if (snapshot.CapturedAt > previous.Value.LastSeen &&
                     snapshot.BytesReceived >= previous.Value.Received &&
                     snapshot.BytesSent >= previous.Value.Sent)
            {
                var receivedDelta = snapshot.BytesReceived - previous.Value.Received;
                var sentDelta = snapshot.BytesSent - previous.Value.Sent;
                var intervalSeconds = (snapshot.CapturedAt - previous.Value.LastSeen).TotalSeconds;
                receivedTotal += receivedDelta;
                sentTotal += sentDelta;

                if (receivedDelta > 0 || sentDelta > 0)
                {
                    AddToMinute(snapshot, receivedDelta, sentDelta, intervalSeconds, transaction);
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
            SELECT b.interface_id, s.name, b.minute_utc, b.bytes_received, b.bytes_sent,
                   b.interval_seconds, b.peak_bytes_per_second, b.longest_interval_seconds,
                   s.description, s.type
            FROM (
                SELECT * FROM traffic_minute
                UNION ALL
                SELECT interface_id, day_utc AS minute_utc, bytes_received, bytes_sent,
                       interval_seconds, peak_bytes_per_second, longest_interval_seconds
                FROM traffic_archive_day
            ) b
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
                reader.GetInt64(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetString(8),
                reader.GetString(9)));
        }

        return buckets;
    }

    public IReadOnlyList<TrafficReportRow> ReadTotalReportRows(DateTimeOffset nowUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH all_traffic AS (
                SELECT interface_id, minute_utc, bytes_received, bytes_sent, peak_bytes_per_second, longest_interval_seconds FROM traffic_minute
                UNION ALL
                SELECT interface_id, day_utc, bytes_received, bytes_sent, peak_bytes_per_second, longest_interval_seconds FROM traffic_archive_day
            )
            SELECT b.interface_id, s.name,
                   SUM(b.bytes_received), SUM(b.bytes_sent), MIN(b.minute_utc),
                   MAX(b.peak_bytes_per_second), MAX(b.longest_interval_seconds),
                   s.description, s.type
            FROM all_traffic b
            JOIN adapter_state s ON s.interface_id = b.interface_id
            GROUP BY b.interface_id, s.name, s.description, s.type
            ORDER BY s.name;
            """;

        using var reader = command.ExecuteReader();
        var rows = new List<TrafficReportRow>();
        while (reader.Read())
        {
            var received = reader.GetInt64(2);
            var sent = reader.GetInt64(3);
            var firstMinute = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var elapsedSeconds = Math.Max(1, (nowUtc - firstMinute).TotalSeconds);
            rows.Add(new TrafficReportRow(
                "all-time", reader.GetString(0), reader.GetString(1), received, sent,
                (received + sent) / elapsedSeconds, reader.GetDouble(5), reader.GetDouble(6),
                reader.GetString(7), reader.GetString(8)));
        }
        return rows;
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
        var messages = TrafficDatabaseSchema.Validate(connection);
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
            var messages = TrafficDatabaseSchema.Validate(readOnlyConnection);
            return new DatabaseCheckResult(
                messages.Count == 1 && string.Equals(messages[0], "ok", StringComparison.OrdinalIgnoreCase),
                messages);
        }
        catch (SqliteException exception)
        {
            return new DatabaseCheckResult(false, [$"SQLite error {exception.SqliteErrorCode}: {exception.Message}"]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DatabaseCheckResult(false, [exception.Message]);
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

    public static DatabaseRestoreResult Restore(string sourcePath, string? destinationPath = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath ?? AppDataPaths.DatabasePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Restore source database was not found.", source);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Restore source must differ from the active database.", nameof(sourcePath));

        var sourceCheck = CheckIntegrity(source);
        if (!sourceCheck.IsHealthy)
            throw new InvalidDataException($"Restore source failed SQLite integrity check: {string.Join("; ", sourceCheck.Messages)}");

        var directory = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Restore destination directory is unavailable.");
        Directory.CreateDirectory(directory);
        var operationId = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(directory, $"octetledger.restore.{operationId}.tmp");
        var previous = File.Exists(destination)
            ? Path.Combine(directory, $"octetledger.before-restore-{DateTime.Now:yyyyMMdd-HHmmss}-{operationId[..8]}.db")
            : null;
        try
        {
            File.Copy(source, temporary, overwrite: false);
            var copiedCheck = CheckIntegrity(temporary);
            if (!copiedCheck.IsHealthy)
                throw new InvalidDataException($"Copied restore database failed SQLite integrity check: {string.Join("; ", copiedCheck.Messages)}");

            if (previous is null) File.Move(temporary, destination);
            else File.Replace(temporary, destination, previous, ignoreMetadataErrors: true);
            return new DatabaseRestoreResult(destination, previous);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public RetentionResult ApplyRetention(int rawDays, DateTimeOffset nowUtc)
    {
        if (rawDays is < 1 or > 3660) throw new ArgumentOutOfRangeException(nameof(rawDays));
        var cutoff = new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero).AddDays(-rawDays);
        using var transaction = connection.BeginTransaction();
        using var archive = connection.CreateCommand();
        archive.Transaction = transaction;
        archive.CommandText = """
            INSERT INTO traffic_archive_day (
                interface_id, day_utc, bytes_received, bytes_sent, interval_seconds,
                peak_bytes_per_second, longest_interval_seconds)
            SELECT interface_id, substr(minute_utc, 1, 10) || 'T00:00:00.0000000+00:00',
                   SUM(bytes_received), SUM(bytes_sent), SUM(interval_seconds),
                   MAX(peak_bytes_per_second), MAX(longest_interval_seconds)
            FROM traffic_minute
            WHERE minute_utc < $cutoff
            GROUP BY interface_id, substr(minute_utc, 1, 10)
            ON CONFLICT(interface_id, day_utc) DO UPDATE SET
                bytes_received = bytes_received + excluded.bytes_received,
                bytes_sent = bytes_sent + excluded.bytes_sent,
                interval_seconds = interval_seconds + excluded.interval_seconds,
                peak_bytes_per_second = MAX(peak_bytes_per_second, excluded.peak_bytes_per_second),
                longest_interval_seconds = MAX(longest_interval_seconds, excluded.longest_interval_seconds);
            """;
        archive.Parameters.AddWithValue("$cutoff", FormatTimestamp(cutoff));
        var days = archive.ExecuteNonQuery();
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM traffic_minute WHERE minute_utc < $cutoff;";
        delete.Parameters.AddWithValue("$cutoff", FormatTimestamp(cutoff));
        var minutes = delete.ExecuteNonQuery();
        transaction.Commit();
        return new RetentionResult(minutes, days);
    }

    public void Vacuum()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM;";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        connection.Dispose();
    }

    internal int ReadSchemaVersion() => TrafficDatabaseSchema.ReadVersion(connection);

    private (long Received, long Sent, DateTimeOffset LastSeen)? ReadState(string interfaceId, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT last_received, last_sent, last_seen_utc
            FROM adapter_state
            WHERE interface_id = $interfaceId;
            """;
        command.Parameters.AddWithValue("$interfaceId", interfaceId);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt64(0), reader.GetInt64(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            : null;
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
        double intervalSeconds,
        SqliteTransaction transaction)
    {
        var utc = snapshot.CapturedAt.UtcDateTime;
        var minute = new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
        var peakBytesPerSecond = (received + sent) / Math.Max(1, intervalSeconds);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO traffic_minute (
                interface_id, minute_utc, bytes_received, bytes_sent, interval_seconds,
                peak_bytes_per_second, longest_interval_seconds)
            VALUES ($id, $minute, $received, $sent, $intervalSeconds, $peak, $intervalSeconds)
            ON CONFLICT(interface_id, minute_utc) DO UPDATE SET
                bytes_received = bytes_received + excluded.bytes_received,
                bytes_sent = bytes_sent + excluded.bytes_sent,
                interval_seconds = interval_seconds + excluded.interval_seconds,
                peak_bytes_per_second = MAX(peak_bytes_per_second, excluded.peak_bytes_per_second),
                longest_interval_seconds = MAX(longest_interval_seconds, excluded.longest_interval_seconds);
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id);
        command.Parameters.AddWithValue("$minute", FormatTimestamp(minute));
        command.Parameters.AddWithValue("$received", received);
        command.Parameters.AddWithValue("$sent", sent);
        command.Parameters.AddWithValue("$intervalSeconds", intervalSeconds);
        command.Parameters.AddWithValue("$peak", peakBytesPerSecond);
        command.ExecuteNonQuery();
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}

public sealed record DatabaseCheckResult(bool IsHealthy, IReadOnlyList<string> Messages);
public sealed record DatabaseRestoreResult(string DatabasePath, string? PreviousDatabaseBackupPath);
public sealed record RetentionResult(int RawMinutesArchived, int DailyRowsWritten);
