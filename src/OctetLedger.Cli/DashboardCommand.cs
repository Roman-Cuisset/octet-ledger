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
                var lastCollectionUtc = store.GetStatus().LastCollectionUtc;
                var collectorState = AppDataPaths.IsPortable
                    ? "Portable/manual collection"
                    : CollectorTaskManager.GetStatus().State;
                var interfaceScope = settings.PreferredInterfaceId is not null
                    ? $"Filtered: {rows.FirstOrDefault(r => r.InterfaceId == settings.PreferredInterfaceId)?.InterfaceName ?? settings.PreferredInterfaceId}"
                    : "Physical interfaces (automatic)";
                var timeZone = TimeZoneInfo.Local.DisplayName;
                Write(context.Response, JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.UtcNow, lastCollectionUtc, collectorState, interfaceScope, timeZone, rows }), "application/json; charset=utf-8");
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
:root{color-scheme:dark;font:16px system-ui;background:#0b1220;color:#e5edf8}body{max-width:1100px;margin:auto;padding:32px}h1{margin-bottom:4px}.muted{color:#8ca0ba}.meta{color:#8ca0ba;font-size:0.85rem;margin-top:8px;line-height:1.6}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:16px;margin:28px 0}.card{background:#131e30;border:1px solid #263750;border-radius:12px;padding:18px}.value{font-size:1.8rem;font-weight:700;margin-top:8px}.chart{display:flex;align-items:end;height:280px;gap:5px;border-bottom:1px solid #40516b;padding-top:20px}.bar{flex:1;min-width:5px;background:linear-gradient(#41b3ff,#2864dc);border-radius:4px 4px 0 0}table{width:100%;border-collapse:collapse;margin-top:28px}td,th{text-align:left;padding:10px;border-bottom:1px solid #263750}th{color:#8ca0ba}
</style></head><body><h1>OctetLedger</h1><div class="muted">Local network usage · last 30 days</div><div class="meta" id="meta"></div><div class="cards"><div class="card">30-day total<div class="value" id="total">—</div></div><div class="card">Daily average<div class="value" id="average">—</div></div><div class="card">Peak day<div class="value" id="peak">—</div></div></div><div class="chart" id="chart"></div><table><thead><tr><th>Day</th><th>Interface</th><th>Download</th><th>Upload</th><th>Total</th></tr></thead><tbody id="rows"></tbody></table><p class="muted" style="font-size:0.85rem;margin-top:16px">Chart bars and cards sum all included interfaces per day. Table rows show each interface separately. Dates are in local time.</p><script>
const fmt=n=>{const u=['B','KiB','MiB','GiB','TiB'];let i=0;while(n>=1024&&i<u.length-1){n/=1024;i++}return n.toFixed(n>=100?0:n>=10?1:2)+' '+u[i]};
fetch('/api/data').then(r=>r.json()).then(({rows,interfaceScope,lastCollectionUtc,collectorState,generatedAt})=>{const value=(r,n)=>r[n]??r[n[0].toLowerCase()+n.slice(1)],today=new Date().toISOString().slice(0,10),daily=new Map;for(const r of rows){const period=value(r,'Period'),total=value(r,'TotalBytes');daily.set(period,(daily.get(period)??0)+total)}const days=[...daily],totals=days.map(([,total])=>total),sum=totals.reduce((a,b)=>a+b,0),max=Math.max(1,...totals);total.textContent=fmt(sum);average.textContent=fmt(sum/Math.max(1,days.length));peak.textContent=fmt(Math.max(0,...totals));chart.innerHTML=days.map(([period,total])=>`<div class="bar" title="${period}: ${fmt(total)}" style="height:${total/max*100}%;${period===today?'opacity:0.5':''}"></div>`).join('');document.querySelector('#rows').innerHTML=[...rows].reverse().map(r=>`<tr><td>${value(r,'Period')}${value(r,'Period')===today?' (partial)':''}</td><td>${value(r,'InterfaceName')}</td><td>${fmt(value(r,'BytesReceived'))}</td><td>${fmt(value(r,'BytesSent'))}</td><td>${fmt(value(r,'TotalBytes'))}</td></tr>`).join('');const col=lastCollectionUtc?'Last collection: '+new Date(lastCollectionUtc).toLocaleString():(collectorState==='Running'?'Collection: never':'Collection: '+collectorState);meta.innerHTML=interfaceScope+' · '+col+' · Generated: '+new Date(generatedAt).toLocaleString()});
</script></body></html>
""";
}
