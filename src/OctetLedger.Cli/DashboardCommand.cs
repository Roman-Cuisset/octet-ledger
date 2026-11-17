using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using OctetLedger.Core;

namespace OctetLedger.Cli;

internal static class DashboardCommand
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        if (!CommandLineArguments.ValidateOptions(arguments, ["--no-open"], ["--port"])) return 2;
        var port = CommandLineArguments.ReadPositiveInteger(arguments, "--port", 8765, 1024, 65535);
        if (port is null) return 2;
        var url = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        try { listener.Start(); }
        catch (HttpListenerException exception)
        {
            Console.Error.WriteLine($"Dashboard could not listen at {url}: {exception.Message}");
            return 1;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        Console.WriteLine($"OctetLedger dashboard: {url}");
        Console.WriteLine("Press Ctrl+C to stop.");
        if (!CommandLineArguments.HasFlag(arguments, "--no-open"))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine($"Could not open a browser: {exception.Message}. Open {url} manually.");
            }
        }
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellation.Token);
                _ = Task.Run(() => Respond(context), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        finally { Console.CancelKeyPress -= cancel; }
        return 0;
    }

    private static void Respond(HttpListenerContext context)
    {
        try
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            if (context.Request.Url?.AbsolutePath == "/api/data")
            {
                using var store = new TrafficStore();
                var now = DateTimeOffset.UtcNow;
                var localToday = now.ToLocalTime().Date;
                var today = localToday.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var start = new DateTimeOffset(localToday.AddDays(-29)).ToUniversalTime();
                var settings = OctetLedgerSettings.Load();
                var buckets = TrafficReport.SelectInterface(store.ReadBuckets(start), null, settings.PreferredInterfaceId);
                var rows = TrafficReport.Daily(buckets).OrderBy(row => row.Period).ToArray();
                var lastCollectionUtc = store.GetStatus().LastCollectionUtc;
                var collector = AppDataPaths.IsPortable
                    ? new CollectorTaskStatus(false, "Portable/manual collection")
                    : CollectorTaskManager.GetStatus();
                var nextExpectedUtc = CollectionDisplay.NextExpectedUtc(collector, lastCollectionUtc);
                var history = store.ReadCollectionHistory(start, now);
                var events = CollectorTaskManager.ReadEvents(start, now);
                var selectedIds = buckets.Select(bucket => bucket.InterfaceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var calendar = Enumerable.Range(0, 30).Select(index =>
                {
                    var date = localToday.AddDays(index - 29);
                    var period = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    var day = history.Days.FirstOrDefault(value => value.Period == period);
                    var from = new DateTimeOffset(date).ToUniversalTime();
                    var to = new DateTimeOffset(date.AddDays(1)).ToUniversalTime();
                    var gaps = GetGapIndicators(history.Gaps, selectedIds, from, to, now);
                    return new
                    {
                        period,
                        observations = day?.Observations ?? 0,
                        measuredIntervals = day?.MeasuredIntervals ?? 0,
                        zeroTrafficIntervals = day?.ZeroTrafficIntervals ?? 0,
                        noActiveInterfaceObservations = day?.NoActiveInterfaceObservations ?? 0,
                        hasCollectorGap = gaps.Collector,
                        hasInterfaceGap = gaps.Interface,
                        hasUnknownCoverage = gaps.UnknownCoverage
                    };
                }).ToArray();
                var preferred = buckets.FirstOrDefault(bucket =>
                    string.Equals(bucket.InterfaceId, settings.PreferredInterfaceId, StringComparison.OrdinalIgnoreCase));
                var interfaceScope = buckets.Count == 0
                    ? "No stored traffic in this period"
                    : preferred is not null
                        ? $"Filtered: {preferred.InterfaceName}"
                        : $"Automatic: {string.Join(", ", buckets.Select(bucket => bucket.InterfaceName).Distinct(StringComparer.OrdinalIgnoreCase))}";
                var timeZone = TimeZoneInfo.Local.DisplayName;
                Write(context.Response, JsonSerializer.Serialize(new
                {
                    generatedAt = now,
                    today,
                    lastCollectionUtc,
                    nextExpectedUtc,
                    collectorState = collector.State,
                    watchdogInstalled = collector.WatchdogInstalled,
                    consecutiveErrors = collector.ConsecutiveErrors,
                    interfaceScope,
                    timeZone,
                    rows,
                    history,
                    events,
                    calendar
                }), "application/json; charset=utf-8");
            }
            else if (DashboardAssets.Files.TryGetValue(context.Request.Url?.AbsolutePath ?? "", out var asset))
                Write(context.Response, asset.Bytes, asset.ContentType);
            else
            {
                context.Response.StatusCode = 404;
                Write(context.Response, "Not found", "text/plain; charset=utf-8");
            }
        }
        catch (Exception exception)
        {
            try
            {
                context.Response.StatusCode = 500;
                Write(context.Response, exception.Message, "text/plain; charset=utf-8");
            }
            catch (Exception responseException) when (responseException is HttpListenerException or IOException or ObjectDisposedException) { }
        }
        finally { context.Response.Close(); }
    }

    private static void Write(HttpListenerResponse response, string value, string contentType) =>
        Write(response, Encoding.UTF8.GetBytes(value), contentType);

    private static void Write(HttpListenerResponse response, byte[] bytes, string contentType)
    {
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
    }

    internal static (bool Collector, bool Interface, bool UnknownCoverage) GetGapIndicators(
        IReadOnlyList<CollectionGap> gaps, IReadOnlySet<string> selectedIds,
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var collector = false;
        var networkInterface = false;
        var unknownCoverage = false;
        foreach (var gap in gaps)
        {
            var end = gap.EndUtc ?? now;
            // Closed zero-duration clock events and a gap opened just now still belong to their start day.
            var instantaneous = end == gap.StartUtc;
            if (gap.StartUtc >= to || gap.StartUtc > now ||
                (instantaneous ? gap.StartUtc < from : end <= from))
                continue;

            if (gap.InterfaceId is not null)
                networkInterface |= selectedIds.Contains(gap.InterfaceId);
            else if (gap.Reason is "unknown_collection_gap" or "clock_not_advanced")
                collector = true;
            else if (gap.Reason == "no_active_interfaces")
                networkInterface = true;
            else if (gap.Reason == "unknown_coverage")
                unknownCoverage = true;
        }
        return (collector, networkInterface, unknownCoverage);
    }
}
