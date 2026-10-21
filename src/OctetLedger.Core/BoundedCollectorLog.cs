using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OctetLedger.Core;

/// <summary>A retained, possibly grouped collector event; Count covers the full first/last timestamp span.</summary>
public sealed record CollectorEvent(DateTimeOffset TimestampUtc, string Kind, long Count,
    DateTimeOffset LastTimestampUtc, string? ErrorType = null);

/// <summary>
/// A cross-process serialized journal with three 256 KiB segments and one bounded active group.
/// Repeated identical records update the active group rather than appending. Collection health is
/// persisted independently, so grouping never conceals consecutive collection failures.
/// </summary>
internal sealed class BoundedCollectorLog
{
    internal const int MaximumFileBytes = 256 * 1024;
    internal const int MaximumRecordBytes = 8192;
    internal const int RetainedFileCount = 3;
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string path;
    private readonly string errorStatePath;
    private readonly string mutexName;
    private readonly int maximumFileBytes;
    private string ActivePath => path;

    internal BoundedCollectorLog(string path, string errorStatePath, int maximumFileBytes = MaximumFileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, MaximumRecordBytes);
        this.path = Path.GetFullPath(path);
        this.errorStatePath = errorStatePath;
        this.maximumFileBytes = maximumFileBytes;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(this.path.ToUpperInvariant())));
        mutexName = $@"Local\OctetLedgerJournal.{key}";
    }

    internal void RecordError(Exception exception, DateTimeOffset timestampUtc)
    {
        WithLock(() =>
        {
            var count = ReadConsecutiveErrorsCore();
            if (count < int.MaxValue) count++;
            var type = Limit(exception.GetType().Name, 96);
            AtomicWrite(errorStatePath, $"{count.ToString(CultureInfo.InvariantCulture)}|{timestampUtc:O}|{type}");
            RecordCore("error", type, exception.Message, timestampUtc);
        });
    }

    internal void RecordSuccess(DateTimeOffset timestampUtc)
    {
        WithLock(() =>
        {
            if (ReadConsecutiveErrorsCore() == 0) return;
            try { RecordCore("recovery", null, "", timestampUtc); }
            finally { File.Delete(errorStatePath); }
        });
    }

    internal void Record(string kind, DateTimeOffset timestampUtc, string? errorType = null, string message = "") =>
        WithLock(() => RecordCore(kind, errorType, message, timestampUtc));

    internal int ReadConsecutiveErrors()
    {
        var count = 0;
        WithLock(() => count = ReadConsecutiveErrorsCore());
        return count;
    }

    internal IReadOnlyList<CollectorEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (toUtc < fromUtc) throw new ArgumentException("The end must not precede the start.", nameof(toUtc));
        var events = new List<CollectorEvent>();
        WithLock(() =>
        {
            for (var index = RetainedFileCount - 1; index >= 0; index--)
            {
                var segment = SegmentPath(index);
                if (!File.Exists(segment) || new FileInfo(segment).Length > maximumFileBytes) continue;
                foreach (var line in File.ReadLines(segment)) AddEvent(Parse(line), fromUtc, toUtc, events);
            }
            AddEvent(ReadActive(), fromUtc, toUtc, events);
        });
        return events;
    }

    private static void AddEvent(LogRecord? record, DateTimeOffset fromUtc, DateTimeOffset toUtc, List<CollectorEvent> events)
    {
        if (record is not null && record.LastTimestampUtc >= fromUtc && record.TimestampUtc <= toUtc)
            events.Add(new CollectorEvent(record.TimestampUtc, record.Kind, record.Count, record.LastTimestampUtc, record.ErrorType));
    }

    private int ReadConsecutiveErrorsCore()
    {
        if (!File.Exists(errorStatePath) || new FileInfo(errorStatePath).Length > MaximumRecordBytes) return 0;
        var value = File.ReadAllText(errorStatePath).Split('|', 2)[0];
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0 ? count : 0;
    }

    private void RecordCore(string kind, string? errorType, string message, DateTimeOffset timestampUtc)
    {
        // The legacy launcher wrote this file without bounds. Do not retain that obsolete writer's output.
        File.Delete(path + ".previous");
        var signature = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(message.AsSpan())));
        kind = Limit(kind, 64);
        errorType = errorType is null ? null : Limit(errorType, 96);
        var active = ReadActive();
        if (active is not null && active.Kind == kind && active.ErrorType == errorType && active.Signature == signature)
        {
            active = active with
            {
                LastTimestampUtc = timestampUtc > active.LastTimestampUtc ? timestampUtc : active.LastTimestampUtc,
                Count = active.Count == long.MaxValue ? long.MaxValue : active.Count + 1
            };
        }
        else
        {
            if (active is not null) AppendCompleted(active);
            active = new LogRecord(timestampUtc, kind, 1, timestampUtc, errorType, Limit(message, 256), signature);
        }
        AtomicWrite(ActivePath, Serialize(active));
    }

    private void AppendCompleted(LogRecord record)
    {
        var text = Serialize(record) + "\n";
        var bytes = Utf8.GetByteCount(text);
        var segment = SegmentPath(0);
        var length = File.Exists(segment) ? new FileInfo(segment).Length : 0;
        if (length > maximumFileBytes)
        {
            File.Delete(segment);
            length = 0;
        }
        if (length + bytes > maximumFileBytes)
        {
            File.Delete(SegmentPath(RetainedFileCount - 1));
            for (var index = RetainedFileCount - 2; index >= 0; index--)
            {
                if (File.Exists(SegmentPath(index))) File.Move(SegmentPath(index), SegmentPath(index + 1), overwrite: true);
            }
        }
        File.AppendAllText(segment, text, Utf8);
    }

    private string SegmentPath(int index) => $"{path}.{index + 1}";

    private LogRecord? ReadActive() => File.Exists(ActivePath) && new FileInfo(ActivePath).Length <= MaximumRecordBytes
        ? Parse(File.ReadAllText(ActivePath)) : null;

    private static LogRecord? Parse(string text)
    {
        if (text.Length > MaximumRecordBytes) return null;
        try
        {
            var record = JsonSerializer.Deserialize<LogRecord>(text);
            return record is { Count: > 0 } && record.Kind is { Length: <= 64 } &&
                record.ErrorType is null or { Length: <= 96 } && record.LastTimestampUtc >= record.TimestampUtc ? record : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Serialize(LogRecord record)
    {
        var text = JsonSerializer.Serialize(record);
        if (Utf8.GetByteCount(text) > MaximumRecordBytes) throw new InvalidDataException("Collector log record exceeds its bound.");
        return text;
    }

    private static string Limit(string text, int length) => text.Length <= length ? text : text[..length];

    private static void AtomicWrite(string destination, string text)
    {
        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, Utf8);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private void WithLock(Action action)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var mutex = new Mutex(false, mutexName);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired) action();
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record LogRecord(DateTimeOffset TimestampUtc, string Kind, long Count,
        DateTimeOffset LastTimestampUtc, string? ErrorType, string Message, string Signature);
}
