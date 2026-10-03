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
                var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var start = new DateTimeOffset(DateTime.Today.AddDays(-29)).ToUniversalTime();
                var settings = OctetLedgerSettings.Load();
                var buckets = TrafficReport.SelectInterface(store.ReadBuckets(start), null, settings.PreferredInterfaceId);
                var rows = TrafficReport.Daily(buckets).OrderBy(row => row.Period).ToArray();
                var lastCollectionUtc = store.GetStatus().LastCollectionUtc;
                var collectorState = AppDataPaths.IsPortable
                    ? "Portable/manual collection"
                    : CollectorTaskManager.GetStatus().State;
                var preferred = buckets.FirstOrDefault(bucket =>
                    string.Equals(bucket.InterfaceId, settings.PreferredInterfaceId, StringComparison.OrdinalIgnoreCase));
                var interfaceScope = buckets.Count == 0
                    ? "No stored traffic in this period"
                    : preferred is not null
                        ? $"Filtered: {preferred.InterfaceName}"
                        : $"Automatic: {string.Join(", ", buckets.Select(bucket => bucket.InterfaceName).Distinct(StringComparer.OrdinalIgnoreCase))}";
                var timeZone = TimeZoneInfo.Local.DisplayName;
                Write(context.Response, JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.UtcNow, today, lastCollectionUtc, collectorState, interfaceScope, timeZone, rows }), "application/json; charset=utf-8");
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
            try
            {
                context.Response.StatusCode = 500;
                Write(context.Response, exception.Message, "text/plain; charset=utf-8");
            }
            catch (Exception responseException) when (responseException is HttpListenerException or IOException or ObjectDisposedException) { }
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


    private const string Html = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>OctetLedger Dashboard</title><style>
:root{color-scheme:dark;font:16px system-ui;background:#0b1220;color:#e5edf8}body{max-width:1100px;margin:auto;padding:32px}h1{margin-bottom:4px}.muted{color:#8ca0ba}.meta{color:#8ca0ba;font-size:0.85rem;margin-top:8px;line-height:1.6}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:16px;margin:28px 0}.card{background:#131e30;border:1px solid #263750;border-radius:12px;padding:18px}.value{font-size:1.8rem;font-weight:700;margin-top:8px}.chart{display:flex;align-items:end;height:280px;gap:5px;border-bottom:1px solid #40516b;padding-top:20px}.bar{flex:1;min-width:5px;background:linear-gradient(#41b3ff,#2864dc);border-radius:4px 4px 0 0}table{width:100%;border-collapse:collapse;margin-top:28px}td,th{text-align:left;padding:10px;border-bottom:1px solid #263750}th{color:#8ca0ba}
</style></head><body><h1>OctetLedger</h1><div class="muted">Local network usage · last 30 days</div><div class="meta" id="meta"></div><div class="cards"><div class="card">30-day total<div class="value" id="total">—</div></div><div class="card">Daily average<div class="value" id="average">—</div></div><div class="card">Peak day<div class="value" id="peak">—</div></div></div><div class="chart" id="chart"></div><table><thead><tr><th>Day</th><th>Interface</th><th>Download</th><th>Upload</th><th>Total</th></tr></thead><tbody id="rows"></tbody></table><p class="muted" style="font-size:0.85rem;margin-top:16px">Chart bars and cards sum all included interfaces per day. Table rows show each interface separately. Dates are in local time.</p><script>
const fmt=n=>{const u=['B','KiB','MiB','GiB','TiB'];let i=0;while(n>=1024&&i<u.length-1){n/=1024;i++}return n.toFixed(n>=100?0:n>=10?1:2)+' '+u[i]};
const element=id=>document.getElementById(id),value=(row,name)=>row[name]??row[name[0].toLowerCase()+name.slice(1)];
fetch('/api/data').then(async response=>{if(!response.ok)throw new Error(await response.text());return response.json()}).then(({rows,today,interfaceScope,lastCollectionUtc,collectorState,generatedAt,timeZone})=>{
  const daily=new Map;
  for(const row of rows){const period=value(row,'Period'),bytes=value(row,'TotalBytes');daily.set(period,(daily.get(period)??0)+bytes)}
  const days=[...daily],totals=days.map(([,bytes])=>bytes),sum=totals.reduce((a,b)=>a+b,0),max=Math.max(1,...totals);
  element('total').textContent=rows.length?fmt(sum):'—';element('average').textContent=rows.length?fmt(sum/Math.max(1,days.length)):'—';element('peak').textContent=rows.length?fmt(Math.max(0,...totals)):'—';
  for(const [period,bytes] of days){const bar=document.createElement('div');bar.className='bar';bar.title=period+': '+fmt(bytes);bar.style.height=bytes/max*100+'%';if(period===today)bar.style.opacity='0.5';element('chart').append(bar)}
  for(const row of [...rows].reverse()){
    const tr=document.createElement('tr'),period=value(row,'Period');
    for(const text of [period+(period===today?' (partial)':''),value(row,'InterfaceName'),fmt(value(row,'BytesReceived')),fmt(value(row,'BytesSent')),fmt(value(row,'TotalBytes'))]){const td=document.createElement('td');td.textContent=text;tr.append(td)}
    element('rows').append(tr);
  }
  const collection='Last collection: '+(lastCollectionUtc?new Date(lastCollectionUtc).toLocaleString():'never')+' · Collector: '+collectorState;
  element('meta').textContent=(rows.length?'':'No measured history in this period. ')+interfaceScope+' · '+collection+' · '+timeZone+' · Generated: '+new Date(generatedAt).toLocaleString();
}).catch(error=>{element('meta').textContent='Unable to load dashboard data: '+error.message});
</script></body></html>
""";
}
