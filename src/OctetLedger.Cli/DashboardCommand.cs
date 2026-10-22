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
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>OctetLedger Dashboard</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
:root{
  --bg:#0d1117;--s1:#161b22;--s2:#1c2333;--bd:#30363d;--bd2:#21262d;
  --fg:#e6edf3;--fg2:#c9d1d9;--mt:#8b949e;
  --blue:#58a6ff;--purple:#bc8cff;--green:#3fb950;--yellow:#d29922;--red:#f85149;
  color-scheme:dark;
}
body{font:15px/1.6 -apple-system,BlinkMacSystemFont,'Segoe UI',system-ui,sans-serif;
  background:var(--bg);color:var(--fg);max-width:1200px;margin:0 auto;padding:32px 24px 80px}

/* Header */
.hdr{display:flex;align-items:baseline;gap:12px;flex-wrap:wrap}
.hdr h1{font-size:1.6rem;font-weight:600;letter-spacing:-.03em;background:linear-gradient(135deg,var(--blue),var(--purple));-webkit-background-clip:text;-webkit-text-fill-color:transparent}
.hdr .sub{color:var(--mt);font-size:.85rem}
.sbar{display:flex;align-items:center;gap:10px;padding:10px 16px;background:var(--s1);
  border:1px solid var(--bd);border-radius:10px;margin:14px 0 24px;font-size:.85rem;flex-wrap:wrap}
.dot{width:9px;height:9px;border-radius:50%;flex-shrink:0;box-shadow:0 0 6px currentColor}
.dot.ok{background:var(--green);color:var(--green)}.dot.warn{background:var(--yellow);color:var(--yellow)}.dot.err{background:var(--red);color:var(--red)}
.sbar .sep{color:var(--bd);margin:0 2px}
#meta{color:var(--mt);font-size:.78rem;margin-bottom:24px}

/* Cards */
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:14px;margin-bottom:32px}
.card{background:var(--s1);border:1px solid var(--bd);border-radius:12px;padding:20px 22px;position:relative;overflow:hidden;transition:border-color .2s}
.card:hover{border-color:#444c56}
.card::before{content:'';position:absolute;top:0;left:0;right:0;height:3px}
.card:nth-child(1)::before{background:linear-gradient(90deg,var(--blue),#79c0ff)}
.card:nth-child(2)::before{background:linear-gradient(90deg,var(--purple),#d2a8ff)}
.card:nth-child(3)::before{background:linear-gradient(90deg,var(--yellow),#e3b341)}
.card .lbl{font-size:.72rem;color:var(--mt);text-transform:uppercase;letter-spacing:.06em;font-weight:500}
.card .val{font-size:2rem;font-weight:700;letter-spacing:-.03em;margin-top:6px}
.card:nth-child(1) .val{color:var(--blue)}.card:nth-child(2) .val{color:var(--purple)}.card:nth-child(3) .val{color:var(--yellow)}

/* Section headings */
h2{font-size:1rem;font-weight:600;color:var(--fg2);margin:28px 0 14px;padding-left:14px;border-left:3px solid var(--blue)}

/* Chart */
.chwrap{background:var(--s1);border:1px solid var(--bd);border-radius:12px;padding:20px 16px 14px;margin-bottom:10px}
.chart{display:flex;align-items:stretch;height:210px;gap:3px}
.day{flex:1;min-width:0;display:flex;flex-direction:column;position:relative}
.bw{flex:1;display:flex;flex-direction:column;justify-content:end;border-radius:4px 4px 0 0;cursor:default}
.br{background:var(--blue);border-radius:4px 4px 0 0;opacity:.8;transition:opacity .15s}
.bs{background:var(--purple);opacity:.8;transition:opacity .15s}
.day:hover .br,.day:hover .bs{opacity:1}
.miss .bw{background:repeating-linear-gradient(135deg,transparent,transparent 4px,var(--bd) 4px,var(--bd) 5px)}
.gday .bw{box-shadow:inset 0 0 0 1.5px var(--yellow)}
.part .br,.part .bs{opacity:.4}.day:hover.part .br,.day:hover.part .bs{opacity:.65}
.ax{font-size:.62rem;text-align:center;color:var(--mt);margin-top:5px;line-height:1}
.tip{position:absolute;bottom:calc(100% - 16px);left:50%;transform:translateX(-50%);background:#1c2333;
  border:1px solid var(--bd);border-radius:8px;padding:8px 12px;font-size:.76rem;white-space:nowrap;
  pointer-events:none;opacity:0;transition:opacity .15s;z-index:10;line-height:1.5;box-shadow:0 4px 12px rgba(0,0,0,.4)}
.day:hover .tip{opacity:1}
.tip b{color:var(--fg)}.tip .dl{color:var(--blue)}.tip .ul{color:var(--purple)}
.lgd{display:flex;gap:18px;font-size:.76rem;color:var(--mt);margin:14px 0 0;flex-wrap:wrap}
.lgd span{display:flex;align-items:center;gap:6px}
.lgd i{display:inline-block;width:12px;height:12px;border-radius:3px;flex-shrink:0}

/* Tables */
.scr{overflow-x:auto;-webkit-overflow-scrolling:touch}
table{width:100%;border-collapse:collapse;font-size:.82rem}
th{text-align:left;padding:10px 12px;color:var(--mt);font-weight:500;font-size:.72rem;text-transform:uppercase;
  letter-spacing:.04em;border-bottom:2px solid var(--bd);position:sticky;top:0;background:var(--bg)}
td{padding:9px 12px;border-bottom:1px solid var(--bd2)}
tbody tr:hover td{background:rgba(88,166,255,.03)}
.n{text-align:right;font-variant-numeric:tabular-nums}

/* Collapsible sections */
details{background:var(--s1);border:1px solid var(--bd);border-radius:12px;margin:14px 0}
details>summary{padding:14px 20px;cursor:pointer;font-weight:500;font-size:.88rem;
  list-style:none;display:flex;align-items:center;gap:10px;user-select:none;color:var(--fg2)}
details>summary::-webkit-details-marker{display:none}
details>summary::before{content:'';display:inline-block;width:6px;height:6px;border-right:2px solid var(--mt);
  border-bottom:2px solid var(--mt);transform:rotate(-45deg);transition:transform .2s;flex-shrink:0}
details[open]>summary::before{transform:rotate(45deg)}
details>summary .cnt{font-size:.72rem;color:var(--mt);background:var(--bg);padding:2px 8px;border-radius:10px;margin-left:auto}
details .inn{padding:4px 20px 18px}
details table th{background:var(--s1)}

/* Timeline */
.tl{list-style:none;font-size:.82rem}
.tl li{padding:10px 0 10px 28px;position:relative;border-left:2px solid var(--bd)}
.tl li::before{content:'';position:absolute;left:-5px;top:15px;width:8px;height:8px;border-radius:50%;background:var(--bd)}
.tl li.w::before{background:var(--yellow)}.tl li.e::before{background:var(--red)}
.tl li:last-child{border-left-color:transparent}
.tl .t{color:var(--fg2);font-weight:500}.tl .r{color:var(--mt)}

/* Health grid */
.hg{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:10px}
.hi{display:flex;justify-content:space-between;padding:11px 16px;background:var(--bg);border-radius:8px;gap:12px;font-size:.84rem}
.hi .hl{color:var(--mt);font-size:.74rem;text-transform:uppercase;letter-spacing:.03em;flex-shrink:0}
.hi .hv{text-align:right;overflow-wrap:anywhere;color:var(--fg2)}
.notice{color:var(--yellow)}

/* Footer */
.ft{margin-top:48px;padding-top:14px;border-top:1px solid var(--bd);color:var(--mt);font-size:.72rem;text-align:center}

@media(max-width:640px){
  body{padding:16px 14px 56px}
  .hdr h1{font-size:1.3rem}
  .cards{grid-template-columns:1fr}
  .card .val{font-size:1.6rem}
  .chart{height:140px;gap:2px}
  .ax{font-size:.48rem}
  .hg{grid-template-columns:1fr}
  .lgd{flex-direction:column;gap:6px}
}
</style>
</head>
<body>

<div class="hdr">
  <h1>OctetLedger</h1>
  <span class="sub">Local network usage · last 30 days</span>
</div>
<div class="sbar">
  <span class="dot" id="dot"></span>
  <span id="state">Loading…</span>
  <span class="sep">·</span>
  <span id="lastin">—</span>
</div>
<div id="meta" role="status"></div>

<div class="cards">
  <div class="card"><div class="lbl">Total (30 days)</div><div class="val" id="total">—</div></div>
  <div class="card"><div class="lbl">Daily average</div><div class="val" id="average">—</div></div>
  <div class="card"><div class="lbl">Peak day</div><div class="val" id="peak">—</div></div>
</div>

<h2>Traffic</h2>
<div class="chwrap">
  <div class="chart" id="chart" role="img" aria-label="30-day traffic chart"></div>
  <div class="lgd">
    <span><i style="background:var(--blue)"></i> Download</span>
    <span><i style="background:var(--purple)"></i> Upload</span>
    <span><i style="background:repeating-linear-gradient(135deg,transparent,transparent 3px,var(--mt) 3px,var(--mt) 4px)"></i> No measurement</span>
    <span><i style="box-shadow:inset 0 0 0 2px var(--yellow)"></i> Interruption</span>
  </div>
</div>

<h2>Daily breakdown</h2>
<div class="scr"><table><thead><tr>
  <th>Day</th><th>Interface</th><th class="n">Download</th><th class="n">Upload</th><th class="n">Total</th><th>Precision</th>
</tr></thead><tbody id="rows"></tbody></table></div>

<details>
  <summary>Collection health<span class="cnt" id="hcnt"></span></summary>
  <div class="inn"><div class="hg" id="health"></div></div>
</details>

<details>
  <summary>Collection observations<span class="cnt" id="ocnt"></span></summary>
  <div class="inn">
    <div class="scr"><table><thead><tr>
      <th>Day</th><th class="n">Observations</th><th class="n">Measured</th><th class="n">Zero-byte</th><th class="n">No active iface</th>
    </tr></thead><tbody id="coverage"></tbody></table></div>
  </div>
</details>

<details>
  <summary>Interruptions & unknown coverage<span class="cnt" id="gcnt"></span></summary>
  <div class="inn">
    <ul class="tl" id="gaps"></ul>
  </div>
</details>

<details>
  <summary>Recent collector events<span class="cnt" id="ecnt"></span></summary>
  <div class="inn">
    <ul class="tl" id="events"></ul>
  </div>
</details>

<div class="ft" id="ft">OctetLedger · 127.0.0.1</div>

<script>
const el=id=>document.getElementById(id),V=(r,n)=>r[n]??r[n[0].toLowerCase()+n.slice(1)];
const F=n=>{const u=['B','KiB','MiB','GiB','TiB'];let i=0;while(n>=1024&&i<u.length-1){n/=1024;i++}return n.toFixed(n>=100?0:n>=10?1:2)+' '+u[i]};
const T=n=>n?new Date(n).toLocaleString():'—';
const P=p=>({'raw_intervals':'Counter intervals','daily_archives':'Daily archives','mixed':'Mixed'}[p]??'Unknown');
const R=r=>({'unknown_collection_gap':'Unknown cause','interface_not_observed':'Not observed','interface_not_up':'Interface down',
  'counter_reset':'Counter reset','clock_not_advanced':'Clock stalled','no_active_interfaces':'No active interface',
  'unknown_coverage':'Unknown coverage'}[r]??r.replaceAll('_',' '));

function addRow(id,txts,nums){const tr=document.createElement('tr');txts.forEach((t,i)=>{
  const td=document.createElement('td');td.textContent=t;if(nums[i])td.className='n';tr.append(td)});el(id).append(tr)}

fetch('/api/data').then(async r=>{if(!r.ok)throw new Error(await r.text());return r.json()}).then(d=>{
  const{rows,today,interfaceScope,lastCollectionUtc,nextExpectedUtc,collectorState,generatedAt,timeZone,calendar,history,events,watchdogInstalled,consecutiveErrors}=d;

  // Status bar
  const running=collectorState&&collectorState.toLowerCase().includes('running');
  el('dot').className='dot '+(running?(consecutiveErrors?'warn':'ok'):'err');
  el('state').textContent=collectorState+(consecutiveErrors?' · '+consecutiveErrors+' consecutive error(s)':'');
  el('lastin').textContent='Last collection: '+T(lastCollectionUtc);

  // Health
  const overdue=nextExpectedUtc&&new Date(nextExpectedUtc)<new Date(generatedAt);
  const hd=[
    ['Next expected',nextExpectedUtc?T(nextExpectedUtc)+(overdue?' (overdue)':''):'First collection pending'],
    ['Watchdog',watchdogInstalled==null?'N/A':watchdogInstalled?'Registered':'Missing'],
    ['Tracking since',T(V(history,'TrackingStartedUtc'))],
    ['Raw detail from',T(V(history,'RawHistoryFromUtc'))],
  ];
  el('health').innerHTML=hd.map(([l,v])=>'<div class="hi"><span class="hl">'+l+'</span><span class="hv'+(v.includes('overdue')?' notice':'')+'">'+v+'</span></div>').join('');
  el('hcnt').textContent=running?'OK':'Attention';

  // Cards — aggregate per day with recv/sent split
  const recvD=new Map,sentD=new Map,totD=new Map;
  for(const r of rows){
    const p=V(r,'Period'),rv=V(r,'BytesReceived'),sv=V(r,'BytesSent');
    recvD.set(p,(recvD.get(p)??0)+rv);sentD.set(p,(sentD.get(p)??0)+sv);totD.set(p,(totD.get(p)??0)+rv+sv);
  }
  const tots=[...totD.values()],sum=tots.reduce((a,b)=>a+b,0),mx=Math.max(1,...tots);
  el('total').textContent=rows.length?F(sum):'—';
  el('average').textContent=rows.length?F(sum/Math.max(1,totD.size)):'—';
  el('peak').textContent=rows.length?F(Math.max(0,...tots)):'—';

  // Chart
  for(const day of calendar){
    const p=day.period,has=totD.has(p),rv=recvD.get(p)??0,sv=sentD.get(p)??0,tot=rv+sv;
    const col=document.createElement('div');
    col.className='day'+(has?'':' miss')+(day.hasGap?' gday':'')+(p===today?' part':'');
    const bw=document.createElement('div');bw.className='bw';
    if(has){
      const rb=document.createElement('div');rb.className='br';rb.style.height=rv/mx*100+'%';
      const sb=document.createElement('div');sb.className='bs';sb.style.height=sv/mx*100+'%';
      bw.append(sb,rb);
    }
    const tp=document.createElement('div');tp.className='tip';
    tp.innerHTML='<b>'+p+'</b><br><span class="dl">\u2193 '+F(rv)+'</span> · <span class="ul">\u2191 '+F(sv)+'</span><br>Total: '+F(tot)+(day.hasGap?'<br>\u26A0 Interruption':'');
    bw.append(tp);
    const ax=document.createElement('div');ax.className='ax';ax.textContent=p.slice(8);
    col.append(bw,ax);el('chart').append(col);
    addRow('coverage',[p,''+day.observations,''+day.measuredIntervals,''+day.zeroTrafficIntervals,''+day.noActiveInterfaceObservations],[0,1,1,1,1]);
  }
  el('ocnt').textContent=calendar.length+' days';

  // Traffic table
  for(const r of[...rows].reverse()){
    const p=V(r,'Period');
    addRow('rows',[p+(p===today?' (partial)':''),V(r,'InterfaceName'),F(V(r,'BytesReceived')),F(V(r,'BytesSent')),F(V(r,'TotalBytes')),P(V(r,'Precision'))+(V(r,'LongestIntervalSeconds')>90?' / long':'')],[0,0,1,1,1,0]);
  }

  // Gaps
  const gaps=V(history,'Gaps');
  for(const g of gaps){
    const nm=V(g,'InterfaceName'),li=document.createElement('li');li.className='w';
    li.innerHTML='<span class="t">'+T(V(g,'StartUtc'))+'</span> \u2192 '+(V(g,'EndUtc')?T(V(g,'EndUtc')):'ongoing')+'<br><span class="r">'+R(V(g,'Reason'))+(nm?' \u00B7 '+nm:'')+'</span>';
    el('gaps').append(li);
  }
  if(!gaps.length){const li=document.createElement('li');li.textContent='No interruption in retained detail.';el('gaps').append(li)}
  el('gcnt').textContent=gaps.length;

  // Events
  const ev=events.slice(-20);
  for(const e of ev){
    const li=document.createElement('li');
    li.innerHTML='<span class="t">'+T(V(e,'TimestampUtc'))+'</span> \u2192 '+T(V(e,'LastTimestampUtc'))+'<br><span class="r">'+V(e,'Kind').replaceAll('_',' ')+' \u00D7'+V(e,'Count')+'</span>';
    el('events').append(li);
  }
  if(!ev.length){const li=document.createElement('li');li.textContent='No events in this period.';el('events').append(li)}
  el('ecnt').textContent=ev.length;

  el('meta').textContent=(rows.length?'':'No measured traffic. ')+interfaceScope+' \u00B7 '+timeZone;
  el('ft').textContent='OctetLedger \u00B7 '+timeZone+' \u00B7 Generated '+T(generatedAt);
}).catch(e=>{el('meta').textContent='Error: '+e.message;el('state').textContent='Unavailable';el('dot').className='dot err'});
</script>
</body></html>
""";
}
