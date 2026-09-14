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
        listener.Start();
        using var cancellation = CreateCancellation();
        Console.WriteLine($"OctetLedger dashboard: {url}");
        Console.WriteLine("Press Ctrl+C to stop.");
        if (!CommandLineArguments.HasFlag(arguments, "--no-open"))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellation.Token);
                _ = Task.Run(() => Respond(context), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
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
                var start = DateTimeOffset.UtcNow.AddDays(-30);
                var settings = OctetLedgerSettings.Load();
                var buckets = TrafficReport.SelectInterface(store.ReadBuckets(start), null, settings.PreferredInterfaceId);
                var rows = TrafficReport.Daily(buckets).OrderBy(row => row.Period).ToArray();
                Write(context.Response, JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.UtcNow, rows }), "application/json; charset=utf-8");
            }
            else if (context.Request.Url?.AbsolutePath == "/")
                Write(context.Response, Html, "text/html; charset=utf-8");
            else
            {
                context.Response.StatusCode = 404;
                Write(context.Response, "Not found", "text/plain; charset=utf-8");
            }
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = 500;
            Write(context.Response, exception.Message, "text/plain; charset=utf-8");
        }
        finally { context.Response.Close(); }
    }

    private static void Write(HttpListenerResponse response, string value, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
    }

    private static CancellationTokenSource CreateCancellation()
    {
        var source = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; source.Cancel(); };
        return source;
    }

    private const string Html = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>OctetLedger Dashboard</title><style>
:root{color-scheme:dark;font:16px system-ui;background:#0b1220;color:#e5edf8}body{max-width:1100px;margin:auto;padding:32px}h1{margin-bottom:4px}.muted{color:#8ca0ba}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:16px;margin:28px 0}.card{background:#131e30;border:1px solid #263750;border-radius:12px;padding:18px}.value{font-size:1.8rem;font-weight:700;margin-top:8px}.chart{display:flex;align-items:end;height:280px;gap:5px;border-bottom:1px solid #40516b;padding-top:20px}.bar{flex:1;min-width:5px;background:linear-gradient(#41b3ff,#2864dc);border-radius:4px 4px 0 0}table{width:100%;border-collapse:collapse;margin-top:28px}td,th{text-align:left;padding:10px;border-bottom:1px solid #263750}th{color:#8ca0ba}
</style></head><body><h1>OctetLedger</h1><div class="muted">Local network usage · last 30 days</div><div class="cards"><div class="card">30-day total<div class="value" id="total">—</div></div><div class="card">Daily average<div class="value" id="average">—</div></div><div class="card">Peak day<div class="value" id="peak">—</div></div></div><div class="chart" id="chart"></div><table><thead><tr><th>Day</th><th>Download</th><th>Upload</th><th>Total</th></tr></thead><tbody id="rows"></tbody></table><script>
const fmt=n=>{const u=['B','KiB','MiB','GiB','TiB'];let i=0;while(n>=1024&&i<u.length-1){n/=1024;i++}return n.toFixed(n>=100?0:n>=10?1:2)+' '+u[i]};
fetch('/api/data').then(r=>r.json()).then(({rows})=>{const totals=rows.map(r=>r.TotalBytes??r.totalBytes),sum=totals.reduce((a,b)=>a+b,0),max=Math.max(1,...totals);total.textContent=fmt(sum);average.textContent=fmt(sum/Math.max(1,rows.length));peak.textContent=fmt(Math.max(0,...totals));chart.innerHTML=rows.map((r,i)=>`<div class="bar" title="${r.Period??r.period}: ${fmt(totals[i])}" style="height:${totals[i]/max*100}%"></div>`).join('');document.querySelector('#rows').innerHTML=[...rows].reverse().map(r=>`<tr><td>${r.Period??r.period}</td><td>${fmt(r.BytesReceived??r.bytesReceived)}</td><td>${fmt(r.BytesSent??r.bytesSent)}</td><td>${fmt(r.TotalBytes??r.totalBytes)}</td></tr>`).join('')});
</script></body></html>
""";
}
