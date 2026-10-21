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
                var calendar = Enumerable.Range(0, 30).Select(index =>
                {
                    var date = localToday.AddDays(index - 29);
                    var period = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    var day = history.Days.FirstOrDefault(value => value.Period == period);
                    var from = new DateTimeOffset(date).ToUniversalTime();
                    var to = new DateTimeOffset(date.AddDays(1)).ToUniversalTime();
                    return new
                    {
                        period,
                        observations = day?.Observations ?? 0,
                        measuredIntervals = day?.MeasuredIntervals ?? 0,
                        zeroTrafficIntervals = day?.ZeroTrafficIntervals ?? 0,
                        noActiveInterfaceObservations = day?.NoActiveInterfaceObservations ?? 0,
                        hasGap = history.Gaps.Any(gap => gap.StartUtc < to && (gap.EndUtc ?? now) > from)
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
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>OctetLedger Dashboard</title>
<style>
:root{color-scheme:dark;font:16px system-ui;background:#0b1220;color:#e5edf8}
body{max-width:1100px;margin:auto;padding:28px}h1{margin-bottom:4px}h2{font-size:1.2rem;margin-top:32px}
.muted,.meta{color:#9cabbe}.meta{font-size:.85rem;line-height:1.6;margin-top:8px}
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:16px;margin:24px 0}
.card,.health{background:#131e30;border:1px solid #263750;border-radius:10px;padding:16px}
.value{font-size:1.8rem;font-weight:700;margin-top:8px}.health dl{display:grid;grid-template-columns:max-content 1fr;gap:8px 20px;margin:0}.health dd{margin:0;overflow-wrap:anywhere}
.chart{display:flex;align-items:stretch;height:230px;gap:4px;margin-top:20px}.day{flex:1;min-width:0;display:flex;flex-direction:column}
.plot{flex:1;display:flex;align-items:end;background:#131e30;border-bottom:2px solid #40516b;border-radius:3px 3px 0 0}
.fill{width:100%;min-height:2px;background:#41b3ff;border-radius:3px 3px 0 0}.missing .plot{background:repeating-linear-gradient(135deg,#131e30,#131e30 5px,#304159 5px,#304159 6px)}
.gap .plot{outline:1px solid #e3b45d;outline-offset:-1px}.partial .fill{opacity:.5}.axis{font-size:.6rem;text-align:center;color:#9cabbe;margin-top:8px}
.legend{font-size:.8rem;line-height:1.7;margin:12px 0;color:#b4c1d2}.scroll{overflow-x:auto}table{width:100%;border-collapse:collapse;margin-top:14px;font-size:.88rem}td,th{text-align:left;padding:10px 8px;border-bottom:1px solid #263750}th{color:#a4b5ca}td{overflow-wrap:anywhere}
.notice{color:#e3b45d}.timeline{padding-left:20px;font-size:.88rem;line-height:1.7}.timeline li{margin:8px 0}.empty{color:#9cabbe;font-size:.9rem}
@media(max-width:600px){body{padding:16px}.health dl{grid-template-columns:1fr;gap:4px}.health dd{margin-bottom:10px}.axis{font-size:.5rem}.chart{gap:2px;height:170px}}
</style>
</head>
<body>
<h1>OctetLedger</h1><div class="muted">Local network usage · last 30 calendar days</div><div class="meta" id="meta" role="status"></div>
<h2>Collection health</h2>
<section class="health"><dl><dt>Collector</dt><dd id="state">Loading…</dd><dt>Last successful collection</dt><dd id="last">—</dd><dt>Next expected collection</dt><dd id="next">—</dd><dt>Independent watchdog</dt><dd id="watchdog">—</dd><dt>Observation detail</dt><dd id="detail">—</dd></dl></section>
<div class="cards"><div class="card">Recorded total (30 days)<div class="value" id="total">—</div></div><div class="card">Average per recorded day<div class="value" id="average">—</div></div><div class="card">Peak recorded day<div class="value" id="peak">—</div></div></div>
<h2>Recorded traffic and missing observations</h2><div class="chart" id="chart" aria-label="30-day traffic and observation history"></div>
<p class="legend">Blue: recorded traffic, including measured zero. Striped: no traffic measurement in the selected scope, not zero. Amber outline: interruption or unknown coverage. Faded: today is partial.</p>
<p class="meta">Coverage below is collector-wide across all adapters; traffic totals and table rows follow the interface selection. Observation counts do not establish continuous coverage. Long-interval bytes are assigned when observed, not redistributed across missing hours.</p>
<div class="scroll"><table><thead><tr><th>Day</th><th>Interface</th><th>Download</th><th>Upload</th><th>Total</th><th>Precision</th></tr></thead><tbody id="rows"></tbody></table></div>
<h2>Collection observations (all adapters)</h2><div class="scroll"><table><thead><tr><th>Day</th><th>Observations</th><th>Measured intervals</th><th>Zero-byte intervals</th><th>No active interface</th></tr></thead><tbody id="coverage"></tbody></table></div>
<h2>Interruptions and unknown coverage</h2><p class="meta">Causes remain unknown unless an explicit event provides evidence. An unknown legacy/retained-detail interval is not proof the collector was stopped.</p><ul class="timeline" id="gaps"></ul>
<h2>Recent collector events</h2><p class="meta">Bounded local journal; showing at most the latest 20 entries. Absence of an event does not establish a cause.</p><ul class="timeline" id="events"></ul>
<p class="meta">Table rows show each interface separately. Daily archive totals cannot be split back into individual hours. Dates are in the server's local calendar; timestamps include the viewer's local display offset.</p>
<script>
const element=id=>document.getElementById(id),value=(row,name)=>row[name]??row[name[0].toLowerCase()+name.slice(1)];
const fmt=n=>{const u=['B','KiB','MiB','GiB','TiB'];let i=0;while(n>=1024&&i<u.length-1){n/=1024;i++}return n.toFixed(n>=100?0:n>=10?1:2)+' '+u[i]};
const timestamp=n=>n?new Date(n).toLocaleString():'unknown';
const precision=p=>({'raw_intervals':'Counter intervals','daily_archives':'Daily archives','mixed':'Intervals + daily archives'}[p]??'Unknown');
const reason=r=>({'unknown_collection_gap':'No successful collection; cause unknown','interface_not_observed':'Interface not observed','interface_not_up':'Interface reported down','counter_reset':'Counters reset; preceding volume unrecoverable','clock_not_advanced':'Clock did not advance; counters rebaselined','no_active_interfaces':'No active interface','unknown_coverage':'Unknown coverage (legacy or retained detail)'}[r]??r.replaceAll('_',' '));
function row(target,texts){const tr=document.createElement('tr');for(const text of texts){const td=document.createElement('td');td.textContent=text;tr.append(td)}element(target).append(tr)}
function item(target,text){const li=document.createElement('li');li.textContent=text;element(target).append(li)}
fetch('/api/data').then(async response=>{if(!response.ok)throw new Error(await response.text());return response.json()}).then(data=>{
  const {rows,today,interfaceScope,lastCollectionUtc,nextExpectedUtc,collectorState,generatedAt,timeZone,calendar,history,events,watchdogInstalled,consecutiveErrors}=data;
  element('state').textContent=collectorState+(consecutiveErrors?' · consecutive errors: '+consecutiveErrors:'');
  element('last').textContent=timestamp(lastCollectionUtc);
  const overdue=nextExpectedUtc&&new Date(nextExpectedUtc)<new Date(generatedAt);
  element('next').textContent=nextExpectedUtc?timestamp(nextExpectedUtc)+(overdue?' (overdue)':''):'Not scheduled / first successful collection pending';
  if(overdue)element('next').className='notice';
  element('watchdog').textContent=watchdogInstalled==null?'Not applicable / not observed':watchdogInstalled?'Registered':'Missing';
  element('detail').textContent='Tracking began '+timestamp(value(history,'TrackingStartedUtc'))+'; retained raw detail from '+timestamp(value(history,'RawHistoryFromUtc'))+'. Earlier coverage is unknown.';
  const daily=new Map;
  for(const record of rows){const period=value(record,'Period');daily.set(period,(daily.get(period)??0)+value(record,'TotalBytes'))}
  const totals=[...daily.values()],sum=totals.reduce((a,b)=>a+b,0),max=Math.max(1,...totals);
  element('total').textContent=rows.length?fmt(sum):'—';element('average').textContent=rows.length?fmt(sum/Math.max(1,daily.size)):'—';element('peak').textContent=rows.length?fmt(Math.max(0,...totals)):'—';
  for(const day of calendar){
    const hasMeasurement=daily.has(day.period),bytes=daily.get(day.period),cell=document.createElement('div'),plot=document.createElement('div'),label=document.createElement('div');
    cell.className='day'+(hasMeasurement?'':' missing')+(day.hasGap?' gap':'')+(day.period===today?' partial':'');
    const text=day.period+': '+(hasMeasurement?fmt(bytes):day.noActiveInterfaceObservations?'No active interface observed':'No traffic measurement in selected scope')+' · collector observations: '+day.observations+(day.hasGap?' · interruption / unknown coverage':'');
    cell.title=text;cell.setAttribute('role','img');cell.setAttribute('aria-label',text);plot.className='plot';
    if(hasMeasurement){const fill=document.createElement('div');fill.className='fill';fill.style.height=bytes/max*100+'%';plot.append(fill)}
    label.className='axis';label.textContent=day.period.slice(8);cell.append(plot,label);element('chart').append(cell);
    row('coverage',[day.period,day.observations,day.measuredIntervals,day.zeroTrafficIntervals,day.noActiveInterfaceObservations]);
  }
  for(const record of [...rows].reverse()){
    row('rows',[value(record,'Period')+(value(record,'Period')===today?' (partial)':''),value(record,'InterfaceName'),fmt(value(record,'BytesReceived')),fmt(value(record,'BytesSent')),fmt(value(record,'TotalBytes')),precision(value(record,'Precision'))+(value(record,'LongestIntervalSeconds')>90?' / long interval':'')]);
  }
  const gaps=value(history,'Gaps');
  for(const gap of gaps){const name=value(gap,'InterfaceName');item('gaps',timestamp(value(gap,'StartUtc'))+' → '+(value(gap,'EndUtc')?timestamp(value(gap,'EndUtc')):'ongoing')+' · '+reason(value(gap,'Reason'))+(name?' · '+name:''))}
  if(!gaps.length)item('gaps','No interruption recorded in retained detail. Earlier or untracked coverage is still unknown.');
  for(const event of events.slice(-20)){item('events',timestamp(value(event,'TimestampUtc'))+' → '+timestamp(value(event,'LastTimestampUtc'))+' · '+value(event,'Kind').replaceAll('_',' ')+' ×'+value(event,'Count'))}
  if(!events.length)item('events','No retained event in this period.');
  element('meta').textContent=(rows.length?'':'No measured traffic in this scope. ')+interfaceScope+' · '+timeZone+' · Generated: '+timestamp(generatedAt);
}).catch(error=>{element('meta').textContent='Unable to load dashboard data: '+error.message;element('state').textContent='Unavailable'});
</script>
</body></html>
""";
}
