using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using OctetLedger.Core;

namespace OctetLedger.Cli;

internal static class ApplicationTrafficCommand
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var action = arguments.FirstOrDefault()?.ToLowerInvariant() ?? "top";
        return action switch
        {
            "top" => ShowTop(arguments.Skip(1).ToArray()),
            "monitor" => await MonitorAsync(arguments.Skip(1).ToArray()),
            _ => Usage()
        };
    }

    private static int ShowTop(string[] arguments)
    {
        if (!CommandLineArguments.ValidateOptions(arguments, [], ["--days", "--count"])) return 2;
        var days = CommandLineArguments.ReadPositiveInteger(arguments, "--days", 30, 1, 3660);
        var count = CommandLineArguments.ReadPositiveInteger(arguments, "--count", 20, 1, 100);
        if (days is null || count is null) return 2;
        var rows = ApplicationTrafficStore.ReadTop(DateTimeOffset.UtcNow.AddDays(-days.Value), count.Value);
        Console.WriteLine($"{"Application",-32} {"Download",12} {"Upload",12} {"Total",12}");
        Console.WriteLine(new string('-', 72));
        foreach (var row in rows)
            Console.WriteLine($"{Trim(row.ProcessName, 32),-32} {ByteFormatter.Format(row.BytesReceived),12} {ByteFormatter.Format(row.BytesSent),12} {ByteFormatter.Format(row.TotalBytes),12}");
        if (rows.Count == 0) Console.WriteLine("No application traffic recorded. Run an elevated 'octetledger apps monitor'.");
        return 0;
    }

    private static async Task<int> MonitorAsync(string[] arguments)
    {
        if (!CommandLineArguments.ValidateOptions(arguments, [], ["--seconds"])) return 2;
        var seconds = CommandLineArguments.ReadPositiveInteger(arguments, "--seconds", 60, 1, 86400);
        if (seconds is null) return 2;
        if (TraceEventSession.IsElevated() != true)
        {
            Console.Error.WriteLine("Application traffic capture requires an elevated Administrator terminal because it uses Windows kernel ETW events.");
            return 1;
        }

        var totals = new ConcurrentDictionary<string, Counters>(StringComparer.OrdinalIgnoreCase);
        var names = new ConcurrentDictionary<int, string>();
        using var session = new TraceEventSession($"OctetLedger-Applications-{Environment.ProcessId}");
        session.StopOnDispose = true;
        void Add(int processId, int size, bool received)
        {
            if (processId <= 0 || size <= 0) return;
            var name = names.GetOrAdd(processId, id =>
            {
                try { return Process.GetProcessById(id).ProcessName + ".exe"; }
                catch (ArgumentException) { return $"pid-{id}"; }
                catch (InvalidOperationException) { return $"pid-{id}"; }
            });
            totals.AddOrUpdate(name,
                _ => received ? new Counters(size, 0) : new Counters(0, size),
                (_, current) => received ? current with { Received = current.Received + size } : current with { Sent = current.Sent + size });
        }
        session.Source.Kernel.TcpIpRecv += data => Add(data.ProcessID, data.size, true);
        session.Source.Kernel.TcpIpSend += data => Add(data.ProcessID, data.size, false);
        session.Source.Kernel.UdpIpRecv += data => Add(data.ProcessID, data.size, true);
        session.Source.Kernel.UdpIpSend += data => Add(data.ProcessID, data.size, false);
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        Console.WriteLine($"Capturing per-application traffic for {seconds} seconds...");
        var processing = Task.Run(() => session.Source.Process());
        await Task.Delay(TimeSpan.FromSeconds(seconds.Value));
        session.Stop();
        await processing;
        var rows = totals.Select(pair => new ApplicationTrafficRow(pair.Key, pair.Value.Received, pair.Value.Sent)).ToArray();
        ApplicationTrafficStore.Add(DateTimeOffset.UtcNow, rows);
        Console.WriteLine($"Recorded {ByteFormatter.Format(rows.Sum(row => row.TotalBytes))} across {rows.Length} applications.");
        return 0;
    }

    private static string Trim(string value, int width) => value.Length <= width ? value : value[..(width - 1)] + "…";
    private static int Usage() { Console.Error.WriteLine("Usage: octetledger apps [top [--days N] [--count N]|monitor [--seconds N]]"); return 2; }
    private sealed record Counters(long Received, long Sent);
}
