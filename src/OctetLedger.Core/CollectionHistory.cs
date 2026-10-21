using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OctetLedger.Core;

public sealed record CollectionHistory(
    DateTimeOffset? LastSuccessUtc,
    IReadOnlyList<CollectionDay> Days,
    IReadOnlyList<CollectionGap> Gaps,
    DateTimeOffset? TrackingStartedUtc = null,
    DateTimeOffset? RawHistoryFromUtc = null);

// Observations count committed collector frames; interval counts are per interface.
public sealed record CollectionDay(
    string Period,
    int Observations,
    int MeasuredIntervals,
    int ZeroTrafficIntervals,
    int NoActiveInterfaceObservations,
    string Precision = "raw_intervals");

public sealed record CollectionGap(
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    string Reason,
    string? InterfaceId,
    string? InterfaceName);

public sealed partial class TrafficStore
{
    public CollectionHistory ReadCollectionHistory(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        ReadCollectionHistory(fromUtc, toUtc, TimeZoneInfo.Local);

    internal CollectionHistory ReadCollectionHistory(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeZoneInfo timeZone)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(toUtc, fromUtc);
        DateTimeOffset? started = null;
        DateTimeOffset? last = null;
        DateTimeOffset? rawFrom = null;
        using (var state = HistoryCommand(null, "SELECT tracking_started_utc, last_success_utc, raw_history_from_utc FROM collection_state WHERE id = 1;"))
        using (var reader = state.ExecuteReader())
        {
            if (reader.Read())
            {
                started = ParseTimestamp(reader.GetString(0));
                last = ParseTimestamp(reader.GetString(1));
                rawFrom = ParseTimestamp(reader.GetString(2));
            }
        }

        var days = new Dictionary<DateTime, (int Observations, int Measured, int Zero, int Inactive, bool Raw, bool Archived)>();
        void AddDay(DateTimeOffset timestamp, int observations, int measured, int zero, int inactive, bool archived)
        {
            var date = TimeZoneInfo.ConvertTime(timestamp, timeZone).Date;
            days.TryGetValue(date, out var previous);
            days[date] = (previous.Observations + observations, previous.Measured + measured,
                previous.Zero + zero, previous.Inactive + inactive, previous.Raw || !archived, previous.Archived || archived);
        }
        using (var samples = HistoryCommand(null, """
            SELECT captured_utc, measured_intervals, zero_intervals, no_active_interfaces
            FROM collection_sample WHERE captured_utc >= $from AND captured_utc < $to;
            """, ("$from", FormatTimestamp(fromUtc)), ("$to", FormatTimestamp(toUtc))))
        using (var reader = samples.ExecuteReader())
        {
            while (reader.Read())
                AddDay(ParseTimestamp(reader.GetString(0)), 1,
                    reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), false);
        }
        var firstDay = CalendarPeriods.StartOfDayUtc(TimeZoneInfo.ConvertTime(fromUtc, timeZone).Date, timeZone);
        using (var archives = HistoryCommand(null, """
            SELECT day_utc, observations, measured_intervals, zero_intervals, no_active_observations
            FROM collection_archive_day WHERE day_utc >= $from AND day_utc < $to;
            """, ("$from", FormatTimestamp(firstDay)), ("$to", FormatTimestamp(toUtc))))
        using (var reader = archives.ExecuteReader())
        {
            while (reader.Read())
                AddDay(ParseTimestamp(reader.GetString(0)), reader.GetInt32(1),
                    reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), true);
        }
        var aggregatedDays = days.OrderBy(day => day.Key).Select(day => new CollectionDay(
            day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), day.Value.Observations, day.Value.Measured,
            day.Value.Zero, day.Value.Inactive,
            day.Value.Raw && day.Value.Archived ? "mixed" : day.Value.Archived ? "daily_archives" : "raw_intervals")).ToArray();

        var gaps = new List<CollectionGap>();
        using (var command = HistoryCommand(null, """
            SELECT start_utc, end_utc, reason, interface_id, interface_name FROM collection_gap
            WHERE start_utc < $to AND (end_utc IS NULL OR end_utc >= $from);
            """, ("$from", FormatTimestamp(fromUtc)), ("$to", FormatTimestamp(toUtc))))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                gaps.Add(new CollectionGap(ParseTimestamp(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : ParseTimestamp(reader.GetString(1)), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        // Archives retain counts, not a claim of continuous or reconstructed interval coverage.
        var knownFrom = rawFrom ?? started;
        if (knownFrom is null || fromUtc < knownFrom.Value)
        {
            var end = knownFrom is null || knownFrom > toUtc ? toUtc : knownFrom.Value;
            if (end > fromUtc) gaps.Add(new CollectionGap(fromUtc, end, "unknown_coverage", null, null));
        }
        if (last is not null && toUtc > last.Value.AddSeconds(90))
        {
            var gapStart = last.Value.AddSeconds(60);
            if (gapStart < toUtc)
                gaps.Add(new CollectionGap(gapStart, null, "unknown_collection_gap", null, null));
        }
        return new CollectionHistory(last, aggregatedDays, gaps.OrderBy(gap => gap.StartUtc).ToArray(), started, rawFrom);
    }

    private void RecordMissingInterfaces(NetworkInterfaceSnapshot[] frame, NetworkInterfaceSnapshot[] observed,
        DateTimeOffset capturedAt, SqliteTransaction transaction)
    {
        var missing = new List<(string Id, string Name)>();
        using (var command = HistoryCommand(transaction, "SELECT interface_id, name FROM adapter_state WHERE last_observed_active = 1;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                if (!observed.Any(snapshot => snapshot.Id == reader.GetString(0)))
                    missing.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var adapter in missing)
        {
            var snapshot = frame.FirstOrDefault(snapshot => snapshot.Id == adapter.Id);
            var reason = snapshot is null ? "interface_not_observed"
                : snapshot.Status != "Up" ? "interface_not_up" : "interface_excluded";
            RecordGap(capturedAt, null, reason, adapter.Id, adapter.Name, transaction);
            using var update = HistoryCommand(transaction,
                "UPDATE adapter_state SET last_observed_active = 0 WHERE interface_id = $id;", ("$id", adapter.Id));
            update.ExecuteNonQuery();
        }
        // A down adapter on the first frame is evidence too, even without a traffic baseline.
        foreach (var snapshot in frame.Where(snapshot => snapshot.Status != "Up" && snapshot.Type != "Loopback"))
            RecordGap(capturedAt, null, "interface_not_up", snapshot.Id, snapshot.Name, transaction);
    }

    private void RecordCollection(DateTimeOffset capturedAt, int active, int measured, int zero, SqliteTransaction transaction)
    {
        using (var previous = HistoryCommand(transaction, "SELECT last_success_utc FROM collection_state WHERE id = 1;"))
        {
            if (previous.ExecuteScalar() is string timestamp)
            {
                var last = ParseTimestamp(timestamp);
                if ((capturedAt - last).TotalSeconds > 90)
                    RecordGap(last.AddSeconds(60), capturedAt, "unknown_collection_gap", null, null, transaction);
                else if (capturedAt <= last)
                    RecordGap(capturedAt, last, "clock_not_advanced", null, null, transaction);
            }
        }
        using (var insert = HistoryCommand(transaction, """
            INSERT INTO collection_sample(captured_utc, measured_intervals, zero_intervals, no_active_interfaces)
            VALUES ($time, $measured, $zero, $inactive);
            INSERT INTO collection_state(id, tracking_started_utc, last_success_utc, raw_history_from_utc)
            VALUES (1, $time, $time, $time)
            ON CONFLICT(id) DO UPDATE SET last_success_utc = excluded.last_success_utc;
            """, ("$time", FormatTimestamp(capturedAt)), ("$measured", measured), ("$zero", zero), ("$inactive", active == 0 ? 1 : 0)))
            insert.ExecuteNonQuery();
        if (active == 0)
            RecordGap(capturedAt, null, "no_active_interfaces", null, null, transaction);
        else
        {
            using var close = HistoryCommand(transaction, """
                UPDATE collection_gap SET end_utc = MAX(start_utc, $time)
                WHERE end_utc IS NULL AND reason = 'no_active_interfaces';
                """, ("$time", FormatTimestamp(capturedAt)));
            close.ExecuteNonQuery();
        }
    }

    private void RecordGap(DateTimeOffset start, DateTimeOffset? end, string reason,
        string? interfaceId, string? interfaceName, SqliteTransaction transaction)
    {
        using var command = HistoryCommand(transaction, """
            INSERT INTO collection_gap(start_utc, end_utc, reason, interface_id, interface_name)
            SELECT $start, $end, $reason, $id, $name
            WHERE $end IS NOT NULL OR NOT EXISTS (
                SELECT 1 FROM collection_gap WHERE end_utc IS NULL AND reason = $reason AND interface_id IS $id);
            """, ("$start", FormatTimestamp(start)), ("$end", end is null ? DBNull.Value : FormatTimestamp(end.Value)),
            ("$reason", reason), ("$id", (object?)interfaceId ?? DBNull.Value), ("$name", (object?)interfaceName ?? DBNull.Value));
        command.ExecuteNonQuery();
    }

    private void CloseInterfaceGaps(string interfaceId, DateTimeOffset capturedAt, SqliteTransaction transaction)
    {
        using var command = HistoryCommand(transaction, """
            UPDATE collection_gap SET end_utc = MAX(start_utc, $time)
            WHERE interface_id = $id AND end_utc IS NULL;
            """, ("$id", interfaceId), ("$time", FormatTimestamp(capturedAt)));
        command.ExecuteNonQuery();
    }

    private void RetainCollectionHistory(DateTimeOffset cutoff, SqliteTransaction transaction)
    {
        using var command = HistoryCommand(transaction, """
            INSERT INTO collection_archive_day(day_utc, observations, measured_intervals, zero_intervals, no_active_observations)
            SELECT retention_day(captured_utc), COUNT(*), SUM(measured_intervals), SUM(zero_intervals), SUM(no_active_interfaces)
            FROM collection_sample WHERE captured_utc < $cutoff GROUP BY retention_day(captured_utc)
            ON CONFLICT(day_utc) DO UPDATE SET
                observations = observations + excluded.observations,
                measured_intervals = measured_intervals + excluded.measured_intervals,
                zero_intervals = zero_intervals + excluded.zero_intervals,
                no_active_observations = no_active_observations + excluded.no_active_observations;
            DELETE FROM collection_sample WHERE captured_utc < $cutoff;
            DELETE FROM collection_gap WHERE end_utc IS NOT NULL AND end_utc < $cutoff;
            UPDATE collection_gap SET start_utc = $cutoff WHERE start_utc < $cutoff;
            UPDATE collection_state SET raw_history_from_utc = MAX(raw_history_from_utc, $cutoff) WHERE id = 1;
            """, ("$cutoff", FormatTimestamp(cutoff)));
        command.ExecuteNonQuery();
    }

    private SqliteCommand HistoryCommand(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
