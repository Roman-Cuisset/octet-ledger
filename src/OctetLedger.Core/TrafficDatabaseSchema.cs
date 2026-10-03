using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OctetLedger.Core;

internal static class TrafficDatabaseSchema
{
    internal const int CurrentVersion = 5;
    private const int ApplicationId = 0x4F4C4447; // OLDG
    private static readonly string[] KnownTables =
    [
        "adapter_state",
        "application_traffic_hour",
        "traffic_archive_day",
        "traffic_minute"
    ];
    private static readonly (string Name, string[] Columns, string[] PrimaryKey)[] RequiredSchema =
    [
        ("adapter_state", ["interface_id", "name", "description", "type", "last_received", "last_sent", "last_seen_utc"], ["interface_id"]),
        ("traffic_minute", ["interface_id", "minute_utc", "bytes_received", "bytes_sent"], ["interface_id", "minute_utc"]),
        ("traffic_archive_day", ["interface_id", "day_utc", "bytes_received", "bytes_sent", "interval_seconds", "peak_bytes_per_second", "longest_interval_seconds"], ["interface_id", "day_utc"]),
        ("application_traffic_hour", ["process_name", "hour_utc", "bytes_received", "bytes_sent"], ["process_name", "hour_utc"])
    ];
    private static readonly string[] MinuteStatisticsColumns =
        ["interval_seconds", "peak_bytes_per_second", "longest_interval_seconds"];

    internal static void Initialize(SqliteConnection connection)
    {
        using (var pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = """
                PRAGMA synchronous = FULL;
                PRAGMA busy_timeout = 5000;
                PRAGMA foreign_keys = ON;
                """;
            pragmas.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        var version = ReadPragma(connection, transaction, "user_version");
        var applicationId = ReadPragma(connection, transaction, "application_id");
        if (version > CurrentVersion)
            throw new InvalidDataException($"Database schema version {version} is newer than this OctetLedger version supports ({CurrentVersion}).");
        if (applicationId != 0 && applicationId != ApplicationId)
            throw new InvalidDataException("The database belongs to another application.");

        var tables = ReadUserTables(connection, transaction);
        if (tables.Any(table => !KnownTables.Contains(table, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("The database contains tables that do not belong to OctetLedger.");
        if (tables.Count > 0 || version > 0)
        {
            var schemaProblem = FindSchemaProblem(connection, transaction, tables, version);
            if (schemaProblem is not null) throw new InvalidDataException(schemaProblem);
        }

        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
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
                    interval_seconds REAL NOT NULL DEFAULT 60,
                    peak_bytes_per_second REAL NOT NULL DEFAULT 0,
                    longest_interval_seconds REAL NOT NULL DEFAULT 60,
                    PRIMARY KEY (interface_id, minute_utc),
                    FOREIGN KEY (interface_id) REFERENCES adapter_state(interface_id)
                );

                CREATE INDEX IF NOT EXISTS ix_traffic_minute_time
                    ON traffic_minute(minute_utc);

                CREATE TABLE IF NOT EXISTS traffic_archive_day (
                    interface_id TEXT NOT NULL,
                    day_utc TEXT NOT NULL,
                    bytes_received INTEGER NOT NULL,
                    bytes_sent INTEGER NOT NULL,
                    interval_seconds REAL NOT NULL,
                    peak_bytes_per_second REAL NOT NULL,
                    longest_interval_seconds REAL NOT NULL,
                    PRIMARY KEY (interface_id, day_utc),
                    FOREIGN KEY (interface_id) REFERENCES adapter_state(interface_id)
                );

                CREATE TABLE IF NOT EXISTS application_traffic_hour (
                    process_name TEXT NOT NULL,
                    hour_utc TEXT NOT NULL,
                    bytes_received INTEGER NOT NULL,
                    bytes_sent INTEGER NOT NULL,
                    PRIMARY KEY(process_name, hour_utc)
                );

                CREATE INDEX IF NOT EXISTS ix_application_traffic_hour_time
                    ON application_traffic_hour(hour_utc);
                """;
            schema.ExecuteNonQuery();
        }

        AddColumnIfMissing(connection, transaction, "traffic_minute", "interval_seconds",
            "ALTER TABLE traffic_minute ADD COLUMN interval_seconds REAL NOT NULL DEFAULT 60;");
        if (AddColumnIfMissing(connection, transaction, "traffic_minute", "peak_bytes_per_second",
                "ALTER TABLE traffic_minute ADD COLUMN peak_bytes_per_second REAL NOT NULL DEFAULT 0;"))
        {
            Execute(connection, transaction, """
                UPDATE traffic_minute
                SET peak_bytes_per_second = CAST(bytes_received + bytes_sent AS REAL) /
                    CASE WHEN interval_seconds < 1 THEN 1 ELSE interval_seconds END;
                """);
        }
        if (AddColumnIfMissing(connection, transaction, "traffic_minute", "longest_interval_seconds",
                "ALTER TABLE traffic_minute ADD COLUMN longest_interval_seconds REAL NOT NULL DEFAULT 60;"))
        {
            Execute(connection, transaction, "UPDATE traffic_minute SET longest_interval_seconds = interval_seconds;");
        }

        Execute(connection, transaction, $"PRAGMA application_id = {ApplicationId}; PRAGMA user_version = {CurrentVersion};");
        transaction.Commit();
        using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode = DELETE;";
        journal.ExecuteNonQuery();
    }

    internal static int ReadVersion(SqliteConnection connection) => ReadPragma(connection, null, "user_version");

    internal static IReadOnlyList<string> Validate(SqliteConnection connection)
    {
        var messages = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) messages.Add(reader.GetString(0));
        }
        if (messages.Count != 1 || !string.Equals(messages[0], "ok", StringComparison.OrdinalIgnoreCase))
            return messages;

        var version = ReadPragma(connection, null, "user_version");
        var applicationId = ReadPragma(connection, null, "application_id");
        var tables = ReadUserTables(connection, null);
        if (version > CurrentVersion)
            return [$"Schema version {version} is newer than supported version {CurrentVersion}."];
        if (applicationId != 0 && applicationId != ApplicationId)
            return ["The database belongs to another application."];
        var schemaProblem = FindSchemaProblem(connection, null, tables, version);
        if (schemaProblem is not null) return [schemaProblem];
        if (tables.Any(table => !KnownTables.Contains(table, StringComparer.OrdinalIgnoreCase)))
            return ["The database contains tables that do not belong to OctetLedger."];
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT interface_id FROM traffic_minute
                WHERE interface_id NOT IN (SELECT interface_id FROM adapter_state)
                UNION ALL
                SELECT interface_id FROM traffic_archive_day
                WHERE interface_id NOT IN (SELECT interface_id FROM adapter_state)
                LIMIT 1;
                """;
            if (tables.Contains("traffic_archive_day"))
            {
                if (command.ExecuteScalar() is not null) return ["Traffic references an adapter that is missing from adapter_state."];
            }
            else
            {
                command.CommandText = """
                    SELECT interface_id FROM traffic_minute
                    WHERE interface_id NOT IN (SELECT interface_id FROM adapter_state)
                    LIMIT 1;
                    """;
                if (command.ExecuteScalar() is not null) return ["Traffic references an adapter that is missing from adapter_state."];
            }
        }
        return ["ok"];
    }

    private static string? FindSchemaProblem(SqliteConnection connection, SqliteTransaction? transaction,
        HashSet<string> tables, int version)
    {
        foreach (var table in RequiredSchema)
        {
            if (!tables.Contains(table.Name))
            {
                if (table.Name is "adapter_state" or "traffic_minute" || version == CurrentVersion)
                    return $"Required OctetLedger table '{table.Name}' is missing.";
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({table.Name});";
            using var reader = command.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryKey = new SortedDictionary<int, string>();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                columns.Add(name);
                var position = reader.GetInt32(5);
                if (position > 0) primaryKey.Add(position, name);
            }
            if (table.Columns.Any(column => !columns.Contains(column)) ||
                table.Name == "traffic_minute" && version == CurrentVersion &&
                MinuteStatisticsColumns.Any(column => !columns.Contains(column)))
                return $"The OctetLedger table '{table.Name}' is missing required columns.";
            if (!primaryKey.Values.SequenceEqual(table.PrimaryKey, StringComparer.OrdinalIgnoreCase))
                return $"The OctetLedger table '{table.Name}' has an invalid primary key.";
        }
        return null;
    }

    private static bool AddColumnIfMissing(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string sql)
    {
        if (ColumnExists(connection, transaction, table, column)) return false;
        Execute(connection, transaction, sql);
        return true;
    }

    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction? transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static HashSet<string> ReadUserTables(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static int ReadPragma(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
