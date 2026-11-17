using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OctetLedger.Core;

public sealed partial class TrafficStore : IDisposable
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

    /// <summary>Stores a captured frame. Frames at or before the last committed collection are ignored.</summary>
    public CollectionResult Collect(IEnumerable<NetworkInterfaceSnapshot> snapshots, DateTimeOffset? capturedAtUtc = null)
    {
        var frame = snapshots.ToArray();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = CollectFrame(frame, capturedAtUtc, transaction, currentCapture: false);
        transaction.Commit();
        return result;
    }

    /// <summary>Serializes current counter acquisition and storage with other SQLite writers.</summary>
    /// <remarks>The callback must read current counters, not return a previously captured frame.</remarks>
    public CollectionResult CollectCurrent(Func<IReadOnlyList<NetworkInterfaceSnapshot>> captureSnapshots)
    {
        ArgumentNullException.ThrowIfNull(captureSnapshots);
        // Acquire the write lock before reading Windows counters, including across collector processes.
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = CollectFrame(captureSnapshots(), null, transaction, currentCapture: true);
        transaction.Commit();
        return result;
    }

    private CollectionResult CollectFrame(IReadOnlyList<NetworkInterfaceSnapshot> frame, DateTimeOffset? capturedAtUtc,
        SqliteTransaction transaction, bool currentCapture)
    {
        var capturedAt = capturedAtUtc ?? (frame.Count == 0 ? DateTimeOffset.UtcNow : frame.Max(snapshot => snapshot.CapturedAt));
        if (!currentCapture)
        {
            using var latest = HistoryCommand(transaction, """
                SELECT COALESCE((SELECT last_success_utc FROM collection_state WHERE id = 1),
                                (SELECT MAX(last_seen_utc) FROM adapter_state));
                """);
            if (latest.ExecuteScalar() is string timestamp && capturedAt <= ParseTimestamp(timestamp))
                return new CollectionResult(0, 0, 0, 0);
        }

        var observed = frame.Where(snapshot => snapshot.Status == "Up" && snapshot.Type != "Loopback").ToArray();
        var receivedTotal = 0L;
        var sentTotal = 0L;
        var baselines = 0;
        var measured = 0;
        var zero = 0;

        RecordMissingInterfaces(frame, observed, capturedAt, transaction);
        foreach (var snapshot in observed)
        {
            var previous = ReadState(snapshot.Id, transaction);
            if (!currentCapture && previous is not null && snapshot.CapturedAt <= previous.Value.LastSeen)
                continue;
            CloseInterfaceGaps(snapshot.Id, snapshot.CapturedAt, transaction);
            if (previous is null)
            {
                baselines++;
            }
            else if (snapshot.CapturedAt <= previous.Value.LastSeen)
            {
                RecordGap(snapshot.CapturedAt, previous.Value.LastSeen, "clock_not_advanced", snapshot.Id, snapshot.Name, transaction);
            }
            else
            {
                var duration = snapshot.CapturedAt - previous.Value.LastSeen;
                if (snapshot.BytesReceived < previous.Value.Received || snapshot.BytesSent < previous.Value.Sent)
                {
                    RecordGap(previous.Value.LastSeen, snapshot.CapturedAt, "counter_reset", snapshot.Id, snapshot.Name, transaction);
                }
                else
                {
                    var receivedDelta = snapshot.BytesReceived - previous.Value.Received;
                    var sentDelta = snapshot.BytesSent - previous.Value.Sent;
                    receivedTotal += receivedDelta;
                    sentTotal += sentDelta;
                    measured++;
                    if (receivedDelta == 0 && sentDelta == 0) zero++;
                    AddToMinute(snapshot, receivedDelta, sentDelta, duration.TotalSeconds, transaction);
                }
            }
            WriteState(snapshot, transaction);
        }

        RecordCollection(capturedAt, observed.Length, measured, zero, transaction);
        return new CollectionResult(observed.Length, baselines, receivedTotal, sentTotal);
    }

    public IReadOnlyList<TrafficBucket> ReadBuckets(DateTimeOffset fromUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.interface_id, s.name, b.minute_utc, b.bytes_received, b.bytes_sent,
                   b.interval_seconds, b.peak_bytes_per_second, b.longest_interval_seconds,
                   s.description, s.type, b.is_daily_archive
            FROM (
                SELECT interface_id, minute_utc, bytes_received, bytes_sent,
                       interval_seconds, peak_bytes_per_second, longest_interval_seconds,
                       0 AS is_daily_archive
                FROM traffic_minute
                UNION ALL
                SELECT interface_id, day_utc AS minute_utc, bytes_received, bytes_sent,
                       interval_seconds, peak_bytes_per_second, longest_interval_seconds,
                       1 AS is_daily_archive
                FROM traffic_archive_day
            ) b
            JOIN adapter_state s ON s.interface_id = b.interface_id
            WHERE b.minute_utc >= $fromUtc
            ORDER BY b.minute_utc DESC, s.name;
            """;
        command.Parameters.AddWithValue("$fromUtc", FormatTimestamp(fromUtc));

        using var reader = command.ExecuteReader();
        var buckets = new List<TrafficBucket>();
        var adapters = new Dictionary<string, (string Name, string Description, string Type)>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!adapters.TryGetValue(id, out var adapter))
            {
                // The metadata comes from the same adapter_state row throughout this query.
                adapter = (reader.GetString(1), reader.GetString(8), reader.GetString(9));
                adapters.Add(id, adapter);
            }
            buckets.Add(new TrafficBucket(
                id,
                adapter.Name,
                ParseTimestamp(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                adapter.Description,
                adapter.Type,
                reader.GetBoolean(10)));
        }

        return buckets;
    }

    public IReadOnlyList<TrafficReportRow> ReadTotalReportRows(DateTimeOffset nowUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH traffic_totals AS (
                SELECT interface_id, MIN(minute_utc) AS first_utc, SUM(bytes_received) AS received,
                       SUM(bytes_sent) AS sent, MAX(peak_bytes_per_second) AS peak,
                       MAX(longest_interval_seconds) AS longest, 0 AS archived
                FROM traffic_minute GROUP BY interface_id
                UNION ALL
                SELECT interface_id, MIN(day_utc), SUM(bytes_received), SUM(bytes_sent),
                       MAX(peak_bytes_per_second), MAX(longest_interval_seconds), 1
                FROM traffic_archive_day GROUP BY interface_id
            )
            SELECT b.interface_id, s.name,
                   SUM(b.received), SUM(b.sent), MIN(b.first_utc), MAX(b.peak), MAX(b.longest),
                   s.description, s.type,
                   CASE WHEN MIN(b.archived) <> MAX(b.archived) THEN 'mixed'
                        WHEN MAX(b.archived) = 1 THEN 'daily_archives' ELSE 'raw_intervals' END
            FROM traffic_totals b
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
            var firstMinute = ParseTimestamp(reader.GetString(4));
            var elapsedSeconds = Math.Max(1, (nowUtc - firstMinute).TotalSeconds);
            rows.Add(new TrafficReportRow(
                "all-time", reader.GetString(0), reader.GetString(1), received, sent,
                (received + sent) / elapsedSeconds, reader.GetDouble(5), reader.GetDouble(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9)));
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
                COALESCE((SELECT last_success_utc FROM collection_state WHERE id = 1),
                         (SELECT MAX(last_seen_utc) FROM adapter_state));
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
            using (var sourceConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = source,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString()))
            using (var stagedConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporary,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString()))
            {
                sourceConnection.Open();
                stagedConnection.Open();
                sourceConnection.BackupDatabase(stagedConnection);
            }
            var copiedCheck = CheckIntegrity(temporary);
            if (!copiedCheck.IsHealthy)
                throw new InvalidDataException($"Copied restore database failed SQLite integrity check: {string.Join("; ", copiedCheck.Messages)}");

            if (previous is null)
            {
                // Without the original main file, its sidecars cannot be safely recovered.
                if (File.Exists(destination + "-wal") || File.Exists(destination + "-shm") || File.Exists(destination + "-journal"))
                    throw new IOException("The destination database is missing but its SQLite journal files are still present. No files were replaced.");
                File.Move(temporary, destination);
            }
            else
            {
                // On Windows, deny data handles while allowing the atomic rename.
                using var destinationGuard = LockRestoreDestination(destination);
                File.Replace(temporary, destination, previous, ignoreMetadataErrors: true);
            }
            return new DatabaseRestoreResult(destination, previous);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static FileStream LockRestoreDestination(string path)
    {
        static FileStream OpenGuard(string databasePath)
        {
            try { return File.Open(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete); }
            catch (IOException exception)
            {
                throw new IOException("The destination database is in use. Close database clients or stop portable monitors and retry restoration; the database was not replaced.", exception);
            }
        }

        static bool HasData(string file) => File.Exists(file) && new FileInfo(file).Length > 0;

        var guard = OpenGuard(path);
        try
        {
            if (HasData(path + "-wal") || HasData(path + "-journal"))
            {
                guard.Dispose();
                // Recover committed WAL pages / hot rollback journals with SQLite itself, never by
                // moving only the main file. No schema initialization or migration is performed here.
                using (var recovery = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWrite,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false,
                    DefaultTimeout = 5
                }.ToString()))
                {
                    recovery.Open();
                    using var command = recovery.CreateCommand();
                    command.CommandText = "PRAGMA schema_version;";
                    command.ExecuteScalar();
                    command.CommandText = "PRAGMA journal_mode=DELETE;";
                    if (!string.Equals(command.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The destination database journals could not be checkpointed. The database was not replaced.");
                }
                guard = OpenGuard(path);
            }
            if (HasData(path + "-wal") || HasData(path + "-journal"))
                throw new IOException("The destination database has pending journal data. The database was not replaced.");
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            File.Delete(path + "-journal");
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    public RetentionResult ApplyRetention(int rawDays, DateTimeOffset nowUtc) =>
        ApplyRetention(rawDays, nowUtc, TimeZoneInfo.Local);

    internal RetentionResult ApplyRetention(int rawDays, DateTimeOffset nowUtc, TimeZoneInfo timeZone, bool onlyIfDue = false)
    {
        if (rawDays is < 1 or > 3660) throw new ArgumentOutOfRangeException(nameof(rawDays));
        var cutoffLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone).Date.AddDays(-rawDays);
        var cutoff = CalendarPeriods.StartOfDayUtc(cutoffLocal, timeZone);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (onlyIfDue)
        {
            // The existing coverage boundary is transactional. Indexed probes also catch legacy/imported
            // rows older than that boundary without scanning or re-aggregating the database on restart.
            using var due = HistoryCommand(transaction, """
                SELECT EXISTS(SELECT 1 FROM collection_state WHERE id = 1 AND raw_history_from_utc < $cutoff)
                    OR EXISTS(SELECT 1 FROM traffic_minute WHERE minute_utc < $cutoff)
                    OR EXISTS(SELECT 1 FROM collection_sample WHERE captured_utc < $cutoff)
                    OR EXISTS(SELECT 1 FROM collection_gap WHERE start_utc < $cutoff);
                """, ("$cutoff", FormatTimestamp(cutoff)));
            if ((long)due.ExecuteScalar()! == 0) return new RetentionResult(0, 0);
        }
        connection.CreateFunction("retention_day", (string timestamp) =>
        {
            var utc = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var localDay = TimeZoneInfo.ConvertTime(utc, timeZone).Date;
            return FormatTimestamp(CalendarPeriods.StartOfDayUtc(localDay, timeZone));
        }, isDeterministic: true);
        using var archive = connection.CreateCommand();
        archive.Transaction = transaction;
        archive.CommandText = """
            INSERT INTO traffic_archive_day (
                interface_id, day_utc, bytes_received, bytes_sent, interval_seconds,
                peak_bytes_per_second, longest_interval_seconds)
            SELECT interface_id, retention_day(minute_utc),
                   SUM(bytes_received), SUM(bytes_sent), SUM(interval_seconds),
                   MAX(peak_bytes_per_second), MAX(longest_interval_seconds)
            FROM traffic_minute
            WHERE minute_utc < $cutoff
            GROUP BY interface_id, retention_day(minute_utc)
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
        RetainCollectionHistory(cutoff, transaction);
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
                interface_id, name, description, type, last_received, last_sent, last_seen_utc, last_observed_active)
            VALUES ($id, $name, $description, $type, $received, $sent, $seen, 1)
            ON CONFLICT(interface_id) DO UPDATE SET
                name = excluded.name,
                description = excluded.description,
                type = excluded.type,
                last_received = excluded.last_received,
                last_sent = excluded.last_sent,
                last_seen_utc = excluded.last_seen_utc,
                last_observed_active = 1;
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
