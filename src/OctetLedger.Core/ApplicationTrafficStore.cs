using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OctetLedger.Core;

public sealed record ApplicationTrafficRow(string ProcessName, long BytesReceived, long BytesSent)
{
    public long TotalBytes => BytesReceived + BytesSent;
}

public static class ApplicationTrafficStore
{
    public static void Add(DateTimeOffset capturedAt, IEnumerable<ApplicationTrafficRow> rows, string? databasePath = null)
    {
        using var connection = Open(databasePath);
        EnsureSchema(connection);
        using var transaction = connection.BeginTransaction();
        foreach (var row in rows.Where(row => row.TotalBytes > 0))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO application_traffic_hour(process_name, hour_utc, bytes_received, bytes_sent)
                VALUES ($name, $hour, $received, $sent)
                ON CONFLICT(process_name, hour_utc) DO UPDATE SET
                    bytes_received = bytes_received + excluded.bytes_received,
                    bytes_sent = bytes_sent + excluded.bytes_sent;
                """;
            var utc = capturedAt.UtcDateTime;
            var hour = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
            command.Parameters.AddWithValue("$name", row.ProcessName);
            command.Parameters.AddWithValue("$hour", hour.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$received", row.BytesReceived);
            command.Parameters.AddWithValue("$sent", row.BytesSent);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static IReadOnlyList<ApplicationTrafficRow> ReadTop(DateTimeOffset fromUtc, int count, string? databasePath = null)
    {
        using var connection = Open(databasePath);
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT process_name, SUM(bytes_received), SUM(bytes_sent)
            FROM application_traffic_hour WHERE hour_utc >= $from
            GROUP BY process_name ORDER BY SUM(bytes_received + bytes_sent) DESC LIMIT $count;
            """;
        command.Parameters.AddWithValue("$from", fromUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$count", count);
        using var reader = command.ExecuteReader();
        var rows = new List<ApplicationTrafficRow>();
        while (reader.Read()) rows.Add(new ApplicationTrafficRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        return rows;
    }

    private static SqliteConnection Open(string? databasePath)
    {
        var path = databasePath ?? AppDataPaths.DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS application_traffic_hour (
                process_name TEXT NOT NULL,
                hour_utc TEXT NOT NULL,
                bytes_received INTEGER NOT NULL,
                bytes_sent INTEGER NOT NULL,
                PRIMARY KEY(process_name, hour_utc));
            CREATE INDEX IF NOT EXISTS ix_application_traffic_hour_time ON application_traffic_hour(hour_utc);
            """;
        command.ExecuteNonQuery();
    }
}
