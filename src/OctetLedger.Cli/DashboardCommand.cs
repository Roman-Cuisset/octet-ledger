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
  --bg:#0d1117;--s1:#161b22;--s2:#1c2333;--s3:#21262d;--bd:#30363d;--bd2:#21262d;
  --fg:#e6edf3;--fg2:#c9d1d9;--mt:#8b949e;
  --blue:#38bdf8;--blue-dark:#0284c7;--purple:#c084fc;--purple-dark:#7c3aed;
  --green:#3fb950;--yellow:#e3b341;--red:#f85149;
  color-scheme:dark;
}
body{font:14px/1.6 -apple-system,BlinkMacSystemFont,'Segoe UI',system-ui,sans-serif;
  background:var(--bg);color:var(--fg);max-width:1200px;margin:0 auto;padding:28px 20px 80px}

/* Header */
.hdr{display:flex;align-items:baseline;gap:12px;flex-wrap:wrap}
.hdr h1{font-size:1.6rem;font-weight:700;letter-spacing:-.03em;background:linear-gradient(135deg,var(--blue),var(--purple));-webkit-background-clip:text;-webkit-text-fill-color:transparent}
.hdr .sub{color:var(--mt);font-size:.85rem}
.sbar{display:flex;align-items:center;gap:10px;padding:9px 16px;background:var(--s1);
  border:1px solid var(--bd);border-radius:10px;margin:12px 0 20px;font-size:.84rem;flex-wrap:wrap}
.dot{width:9px;height:9px;border-radius:50%;flex-shrink:0;box-shadow:0 0 6px currentColor}
.dot.ok{background:var(--green);color:var(--green)}.dot.warn{background:var(--yellow);color:var(--yellow)}.dot.err{background:var(--red);color:var(--red)}
.sbar .sep{color:var(--bd);margin:0 2px}
#meta{color:var(--mt);font-size:.78rem;margin-bottom:20px}

/* Cards */
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:14px;margin-bottom:28px}
.card{background:var(--s1);border:1px solid var(--bd);border-radius:12px;padding:18px 20px;position:relative;overflow:hidden}
.card::before{content:'';position:absolute;top:0;left:0;right:0;height:3px}
.card:nth-child(1)::before{background:linear-gradient(90deg,var(--blue),#7dd3fc)}
.card:nth-child(2)::before{background:linear-gradient(90deg,var(--purple),#e9d5ff)}
.card:nth-child(3)::before{background:linear-gradient(90deg,var(--yellow),#fde047)}
.card .lbl{font-size:.72rem;color:var(--mt);text-transform:uppercase;letter-spacing:.06em;font-weight:600}
.card .val{font-size:1.85rem;font-weight:700;letter-spacing:-.03em;margin-top:4px}
.card:nth-child(1) .val{color:var(--blue)}.card:nth-child(2) .val{color:var(--purple)}.card:nth-child(3) .val{color:var(--yellow)}

/* Section headings */
h2{font-size:.95rem;font-weight:600;color:var(--fg2);margin:28px 0 12px;padding-left:12px;border-left:3px solid var(--blue)}

/* Chart card */
.chcard{background:var(--s1);border:1px solid var(--bd);border-radius:14px;padding:20px 20px 16px;position:relative}
.ch-head{display:flex;justify-content:space-between;align-items:center;gap:14px;flex-wrap:wrap;margin-bottom:14px}
.ch-title{font-size:1.05rem;font-weight:600;color:var(--fg)}
.ch-sub{font-size:.78rem;color:var(--mt);margin-top:2px}
.ch-toggles{display:inline-flex;background:var(--bg);border:1px solid var(--bd);border-radius:8px;padding:3px;gap:2px}
.tbtn{background:none;border:none;color:var(--mt);padding:5px 12px;font-size:.76rem;border-radius:6px;cursor:pointer;font-weight:500;transition:all .15s}
.tbtn:hover{color:var(--fg)}
.tbtn.active{background:var(--s3);color:var(--fg);font-weight:600;box-shadow:0 1px 3px rgba(0,0,0,.3)}

/* Quick stats row */
.ch-pills{display:flex;gap:12px;flex-wrap:wrap;margin-bottom:16px;padding-bottom:14px;border-bottom:1px solid var(--bd2);font-size:.78rem}
.pill{display:inline-flex;align-items:center;gap:6px;background:var(--bg);padding:4px 10px;border-radius:6px;border:1px solid var(--bd2)}
.pill .pdot{width:7px;height:7px;border-radius:50%}
.pill b{color:var(--fg)}

/* SVG wrapper */
.ch-svg-wrap{position:relative;width:100%;user-select:none}
#chart-svg{width:100%;height:270px;display:block;overflow:visible}
.col-hit{fill:transparent;cursor:pointer}
.col-hit:hover,.col-hit.active{fill:rgba(56,189,248,.07)}

/* Tooltip */
.c-tip{position:absolute;background:rgba(22,27,34,.95);backdrop-filter:blur(10px);
  border:1px solid #444c56;border-radius:10px;padding:10px 14px;font-size:.78rem;
  pointer-events:none;opacity:0;transition:opacity .15s,transform .1s;z-index:20;
  box-shadow:0 8px 24px rgba(0,0,0,.6);min-width:185px;line-height:1.5}
.c-tip.visible{opacity:1}
.tip-hdr{font-weight:600;color:var(--fg);display:flex;justify-content:space-between;align-items:center;gap:8px;margin-bottom:6px;padding-bottom:5px;border-bottom:1px solid var(--bd2)}
.tip-badge{font-size:.65rem;padding:1px 6px;border-radius:4px;font-weight:600;text-transform:uppercase}
.tip-badge.today{background:rgba(56,189,248,.2);color:var(--blue)}
.tip-badge.peak{background:rgba(227,179,65,.2);color:var(--yellow)}
.tip-row{display:flex;justify-content:space-between;align-items:center;gap:10px;margin:3px 0}
.tip-row .c-dot{width:7px;height:7px;border-radius:50%;display:inline-block;margin-right:4px}
.tip-row.total{margin-top:6px;padding-top:5px;border-top:1px solid var(--bd2);font-weight:600;color:var(--fg)}
.tip-pct{color:var(--mt);font-size:.72rem}
.tip-warn{margin-top:6px;color:var(--yellow);font-size:.72rem;display:flex;align-items:center;gap:4px}

/* Chart legend */
.lgd{display:flex;gap:16px;font-size:.76rem;color:var(--mt);margin:14px 4px 0;flex-wrap:wrap}
.lgd span{display:flex;align-items:center;gap:6px}
.lgd i{display:inline-block;width:10px;height:10px;border-radius:2px;flex-shrink:0}

/* Tables */
.scr{overflow-x:auto;-webkit-overflow-scrolling:touch}
table{width:100%;border-collapse:collapse;font-size:.82rem}
th{text-align:left;padding:10px 12px;color:var(--mt);font-weight:600;font-size:.72rem;text-transform:uppercase;
  letter-spacing:.04em;border-bottom:2px solid var(--bd);position:sticky;top:0;background:var(--bg)}
td{padding:8px 12px;border-bottom:1px solid var(--bd2)}
tbody tr:hover td{background:rgba(56,189,248,.03)}
.n{text-align:right;font-variant-numeric:tabular-nums}

/* Collapsible sections */
details{background:var(--s1);border:1px solid var(--bd);border-radius:12px;margin:12px 0}
details>summary{padding:12px 18px;cursor:pointer;font-weight:500;font-size:.86rem;
  list-style:none;display:flex;align-items:center;gap:10px;user-select:none;color:var(--fg2)}
details>summary::-webkit-details-marker{display:none}
details>summary::before{content:'';display:inline-block;width:6px;height:6px;border-right:2px solid var(--mt);
  border-bottom:2px solid var(--mt);transform:rotate(-45deg);transition:transform .2s;flex-shrink:0}
details[open]>summary::before{transform:rotate(45deg)}
details>summary .cnt{font-size:.72rem;color:var(--mt);background:var(--bg);padding:2px 8px;border-radius:10px;margin-left:auto}
details .inn{padding:4px 18px 16px}
details table th{background:var(--s1)}

/* Timeline */
.tl{list-style:none;font-size:.82rem}
.tl li{padding:8px 0 8px 24px;position:relative;border-left:2px solid var(--bd)}
.tl li::before{content:'';position:absolute;left:-5px;top:14px;width:8px;height:8px;border-radius:50%;background:var(--bd)}
.tl li.w::before{background:var(--yellow)}.tl li.e::before{background:var(--red)}
.tl li:last-child{border-left-color:transparent}
.tl .t{color:var(--fg2);font-weight:500}.tl .r{color:var(--mt)}

/* Health grid */
.hg{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:10px}
.hi{display:flex;justify-content:space-between;padding:10px 14px;background:var(--bg);border-radius:8px;gap:12px;font-size:.82rem}
.hi .hl{color:var(--mt);font-size:.72rem;text-transform:uppercase;letter-spacing:.03em;flex-shrink:0}
.hi .hv{text-align:right;overflow-wrap:anywhere;color:var(--fg2)}
.notice{color:var(--yellow)}

/* Footer */
.ft{margin-top:40px;padding-top:14px;border-top:1px solid var(--bd);color:var(--mt);font-size:.72rem;text-align:center}

@media(max-width:640px){
  body{padding:16px 12px 56px}
  .hdr h1{font-size:1.3rem}
  .cards{grid-template-columns:1fr}
  .card .val{font-size:1.5rem}
  #chart-svg{height:220px}
  .hg{grid-template-columns:1fr}
  .lgd{flex-direction:column;gap:6px}
  .ch-head{flex-direction:column;align-items:flex-start}
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
<div class="chcard">
  <div class="ch-head">
    <div>
      <div class="ch-title">Network Traffic</div>
      <div class="ch-sub" id="ch-sub">Last 30 calendar days</div>
    </div>
    <div class="ch-toggles">
      <button class="tbtn active" data-mode="stacked" id="btn-stacked">Stacked</button>
      <button class="tbtn" data-mode="grouped" id="btn-grouped">Side-by-side</button>
      <button class="tbtn" data-mode="area" id="btn-area">Trend</button>
    </div>
  </div>
  <div class="ch-pills" id="ch-pills"></div>
  <div class="ch-svg-wrap" id="ch-wrap">
    <svg id="chart-svg" viewBox="0 0 960 270" preserveAspectRatio="none">
      <defs>
        <linearGradient id="g-dl" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#38bdf8"/>
          <stop offset="100%" stop-color="#0284c7"/>
        </linearGradient>
        <linearGradient id="g-ul" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#c084fc"/>
          <stop offset="100%" stop-color="#7c3aed"/>
        </linearGradient>
        <linearGradient id="g-dl-area" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#38bdf8" stop-opacity="0.4"/>
          <stop offset="100%" stop-color="#0284c7" stop-opacity="0.0"/>
        </linearGradient>
        <linearGradient id="g-ul-area" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#c084fc" stop-opacity="0.35"/>
          <stop offset="100%" stop-color="#7c3aed" stop-opacity="0.0"/>
        </linearGradient>
        <pattern id="g-hatch" width="6" height="6" patternTransform="rotate(45 0 0)" patternUnits="userSpaceOnUse">
          <line x1="0" y1="0" x2="0" y2="6" stroke="#484f58" stroke-width="1.5"/>
        </pattern>
      </defs>
      <g id="svg-grid"></g>
      <g id="svg-bars"></g>
      <g id="svg-hits"></g>
    </svg>
    <div id="chart-tip" class="c-tip"></div>
  </div>
  <div class="lgd">
    <span><i style="background:var(--blue)"></i> Download</span>
    <span><i style="background:var(--purple)"></i> Upload</span>
    <span><i style="background:repeating-linear-gradient(135deg,transparent,transparent 3px,var(--mt) 3px,var(--mt) 4px)"></i> No measurement</span>
    <span><i style="width:7px;height:7px;border-radius:50%;background:var(--yellow)"></i> Interruption</span>
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
const F=n=>{if(n<=0||isNaN(n))return'0 B';const u=['B','KiB','MiB','GiB','TiB'];let i=0;while(n>=1024&&i<u.length-1){n/=1024;i++}return n.toFixed(n>=100?0:n>=10?1:2)+' '+u[i]};
const T=n=>n?new Date(n).toLocaleString():'—';
const P=p=>({'raw_intervals':'Counter intervals','daily_archives':'Daily archives','mixed':'Mixed'}[p]??'Unknown');
const R=r=>({'unknown_collection_gap':'Unknown cause','interface_not_observed':'Not observed','interface_not_up':'Interface down',
  'counter_reset':'Counter reset','clock_not_advanced':'Clock stalled','no_active_interfaces':'No active interface',
  'unknown_coverage':'Unknown coverage'}[r]??r.replaceAll('_',' '));

function addRow(id,txts,nums){const tr=document.createElement('tr');txts.forEach((t,i)=>{
  const td=document.createElement('td');td.textContent=t;if(nums[i])td.className='n';tr.append(td)});el(id).append(tr)}

let chartState = { mode: 'stacked', data: null };

function renderSvgChart() {
  if (!chartState.data) return;
  const { calendar, recvD, sentD, totD, today, mx, peakPeriod } = chartState.data;
  const mode = chartState.mode;
  const leftX = 66, rightX = 944, topY = 22, botY = 220, chartH = botY - topY, w = rightX - leftX;
  const n = calendar.length, step = w / n;

  // Grid lines & labels
  const gridG = el('svg-grid');
  let gridHtml = '';
  for (let i = 0; i <= 4; i++) {
    const frac = i / 4;
    const y = botY - frac * chartH;
    const val = frac * mx;
    gridHtml += `<line x1="${leftX}" y1="${y}" x2="${rightX}" y2="${y}" stroke="${i===0?'#384457':'#212c3d'}" ${i>0?'stroke-dasharray="4,4"':''} stroke-width="${i===0?'1.5':'1'}"/>`;
    gridHtml += `<text x="${leftX - 8}" y="${y + 3.5}" text-anchor="end" fill="var(--mt)" font-size="10" font-family="system-ui">${F(val)}</text>`;
  }
  gridG.innerHTML = gridHtml;

  // Data representation
  const barsG = el('svg-bars');
  const hitsG = el('svg-hits');
  let barsHtml = '', hitsHtml = '';

  if (mode === 'area') {
    const dlPts = [], ulPts = [];
    calendar.forEach((d, i) => {
      const cx = leftX + i * step + step / 2;
      const rv = recvD.get(d.period) ?? 0, sv = sentD.get(d.period) ?? 0;
      const rH = (rv / mx) * chartH, sH = (sv / mx) * chartH;
      dlPts.push({ x: cx, y: botY - rH });
      ulPts.push({ x: cx, y: botY - sH });
    });
    function buildSpline(pts) {
      if (!pts.length) return { line: '', area: '' };
      let d = `M ${pts[0].x} ${pts[0].y}`;
      for (let i = 0; i < pts.length - 1; i++) {
        const p0 = pts[i === 0 ? 0 : i - 1], p1 = pts[i], p2 = pts[i + 1], p3 = pts[i + 2 >= pts.length ? pts.length - 1 : i + 2];
        const cp1x = p1.x + (p2.x - p0.x) / 6, cp1y = p1.y + (p2.y - p0.y) / 6;
        const cp2x = p2.x - (p3.x - p1.x) / 6, cp2y = p2.y - (p3.y - p1.y) / 6;
        d += ` C ${cp1x.toFixed(1)} ${cp1y.toFixed(1)}, ${cp2x.toFixed(1)} ${cp2y.toFixed(1)}, ${p2.x.toFixed(1)} ${p2.y.toFixed(1)}`;
      }
      return { line: d, area: d + ` L ${pts[pts.length - 1].x} ${botY} L ${pts[0].x} ${botY} Z` };
    }
    const dlS = buildSpline(dlPts), ulS = buildSpline(ulPts);
    barsHtml += `<path d="${dlS.area}" fill="url(#g-dl-area)"/>`;
    barsHtml += `<path d="${dlS.line}" fill="none" stroke="var(--blue)" stroke-width="2.5"/>`;
    barsHtml += `<path d="${ulS.area}" fill="url(#g-ul-area)"/>`;
    barsHtml += `<path d="${ulS.line}" fill="none" stroke="var(--purple)" stroke-width="2"/>`;
    dlPts.forEach((p, i) => {
      if (recvD.has(calendar[i].period)) barsHtml += `<circle cx="${p.x}" cy="${p.y}" r="3" fill="var(--blue)"/>`;
    });
    ulPts.forEach((p, i) => {
      if (sentD.has(calendar[i].period)) barsHtml += `<circle cx="${p.x}" cy="${p.y}" r="2.5" fill="var(--purple)"/>`;
    });
  }

  calendar.forEach((d, i) => {
    const p = d.period, has = totD.has(p);
    const rv = recvD.get(p) ?? 0, sv = sentD.get(p) ?? 0, tot = rv + sv;
    const cx = leftX + i * step + step / 2;
    const isToday = p === today;

    if (mode === 'stacked') {
      const bw = Math.min(18, Math.max(8, step - 6));
      const bx = cx - bw / 2;
      const rH = Math.max(0, (rv / mx) * chartH);
      const sH = Math.max(0, (sv / mx) * chartH);
      if (has && tot > 0) {
        const topH = sH > 0 ? sH : rH;
        if (rH > 0) {
          const rxB = sH > 0 ? 0 : 3;
          barsHtml += `<rect x="${bx}" y="${botY - rH}" width="${bw}" height="${rH}" fill="url(#g-dl)" rx="${rxB}" opacity="${isToday?0.75:1}"/>`;
        }
        if (sH > 0) {
          barsHtml += `<rect x="${bx}" y="${botY - rH - sH}" width="${bw}" height="${sH}" fill="url(#g-ul)" rx="3" opacity="${isToday?0.75:1}"/>`;
        }
      } else if (!has || d.observations === 0) {
        barsHtml += `<rect x="${bx}" y="${botY - 6}" width="${bw}" height="6" fill="url(#g-hatch)" rx="2" opacity="0.6"/>`;
      } else {
        // Zero measured traffic
        barsHtml += `<rect x="${bx}" y="${botY - 2}" width="${bw}" height="2" fill="var(--mt)" rx="1" opacity="0.5"/>`;
      }
    } else if (mode === 'grouped') {
      const bw = Math.min(9, Math.max(4, (step - 8) / 2));
      const rH = Math.max(0, (rv / mx) * chartH);
      const sH = Math.max(0, (sv / mx) * chartH);
      if (has && tot > 0) {
        if (rH > 0) barsHtml += `<rect x="${cx - bw - 1}" y="${botY - rH}" width="${bw}" height="${rH}" fill="url(#g-dl)" rx="2.5" opacity="${isToday?0.75:1}"/>`;
        if (sH > 0) barsHtml += `<rect x="${cx + 1}" y="${botY - sH}" width="${bw}" height="${sH}" fill="url(#g-ul)" rx="2.5" opacity="${isToday?0.75:1}"/>`;
      } else if (!has || d.observations === 0) {
        barsHtml += `<rect x="${cx - bw}" y="${botY - 6}" width="${bw * 2 + 2}" height="6" fill="url(#g-hatch)" rx="2" opacity="0.6"/>`;
      } else {
        barsHtml += `<rect x="${cx - bw}" y="${botY - 2}" width="${bw * 2 + 2}" height="2" fill="var(--mt)" rx="1" opacity="0.5"/>`;
      }
    }

    // Interruption marker dot
    if (d.hasGap) {
      barsHtml += `<circle cx="${cx}" cy="${botY + 8}" r="2.5" fill="var(--yellow)"/>`;
    }

    // Date label
    const dayNum = p.slice(8);
    const lblColor = isToday ? 'var(--blue)' : 'var(--mt)';
    const lblWeight = isToday ? '700' : '400';
    barsHtml += `<text x="${cx}" y="${botY + 22}" text-anchor="middle" fill="${lblColor}" font-weight="${lblWeight}" font-size="9.5" font-family="system-ui">${dayNum}</text>`;

    // Interactive hover zone
    hitsHtml += `<rect x="${leftX + i * step}" y="${topY - 6}" width="${step}" height="${chartH + 34}" class="col-hit" data-idx="${i}"/>`;
  });

  barsG.innerHTML = barsHtml;
  hitsG.innerHTML = hitsHtml;

  // Setup hover listeners
  const wrap = el('ch-wrap');
  const tip = el('chart-tip');
  const hits = hitsG.querySelectorAll('.col-hit');

  hits.forEach(hit => {
    hit.addEventListener('mouseenter', e => {
      const i = parseInt(hit.getAttribute('data-idx'));
      const d = calendar[i], p = d.period;
      const rv = recvD.get(p) ?? 0, sv = sentD.get(p) ?? 0, tot = rv + sv;
      const has = totD.has(p);
      const isToday = p === today;
      const isPeak = p === peakPeriod && tot > 0;
      const pctDl = tot > 0 ? Math.round(rv / tot * 100) : 0;
      const pctUl = tot > 0 ? Math.round(sv / tot * 100) : 0;

      let tipHtml = `<div class="tip-hdr"><span>${p}</span>`;
      if (isToday) tipHtml += `<span class="tip-badge today">Today</span>`;
      if (isPeak) tipHtml += `<span class="tip-badge peak">Peak</span>`;
      tipHtml += `</div>`;

      if (has && tot > 0) {
        tipHtml += `<div class="tip-row"><span><i class="c-dot" style="background:var(--blue)"></i>Download</span><b>${F(rv)}</b><span class="tip-pct">${pctDl}%</span></div>`;
        tipHtml += `<div class="tip-row"><span><i class="c-dot" style="background:var(--purple)"></i>Upload</span><b>${F(sv)}</b><span class="tip-pct">${pctUl}%</span></div>`;
        tipHtml += `<div class="tip-row total"><span>Total</span><b>${F(tot)}</b></div>`;
      } else if (!has) {
        tipHtml += `<div class="tip-row" style="color:var(--mt)"><i>No measurement in selected scope</i></div>`;
      } else {
        tipHtml += `<div class="tip-row" style="color:var(--mt)"><i>Measured 0 bytes traffic</i></div>`;
      }

      tipHtml += `<div class="tip-row" style="font-size:0.7rem;color:var(--mt);margin-top:4px">${d.observations} observation(s)</div>`;
      if (d.hasGap) {
        tipHtml += `<div class="tip-warn">⚠️ Interruption recorded</div>`;
      }

      tip.innerHTML = tipHtml;
      tip.classList.add('visible');

      // Position tip
      const wrapRect = wrap.getBoundingClientRect();
      const colCenterRel = (leftX + i * step + step / 2) / 960 * wrapRect.width;
      const tipW = 200;
      const tipX = Math.max(10, Math.min(colCenterRel - tipW / 2, wrapRect.width - tipW - 10));
      tip.style.left = tipX + 'px';
      tip.style.top = '10px';
    });

    hit.addEventListener('mouseleave', () => {
      tip.classList.remove('visible');
    });
  });
}

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

  // Aggregations
  const recvD=new Map,sentD=new Map,totD=new Map;
  let totDl = 0, totUl = 0;
  for(const r of rows){
    const p=V(r,'Period'),rv=V(r,'BytesReceived'),sv=V(r,'BytesSent');
    recvD.set(p,(recvD.get(p)??0)+rv);sentD.set(p,(sentD.get(p)??0)+sv);totD.set(p,(totD.get(p)??0)+rv+sv);
    totDl += rv; totUl += sv;
  }
  const tots=[...totD.values()],sum=tots.reduce((a,b)=>a+b,0),mx=Math.max(1024,...tots);
  let peakPeriod = '', peakVal = 0;
  for(const [p,v] of totD.entries()){if(v>peakVal){peakVal=v;peakPeriod=p}}

  el('total').textContent=rows.length?F(sum):'—';
  el('average').textContent=rows.length?F(sum/Math.max(1,totD.size)):'—';
  el('peak').textContent=rows.length?F(peakVal):'—';

  // Quick pills above chart
  const dlPct = sum > 0 ? (totDl / sum * 100).toFixed(1) : 0;
  const ulPct = sum > 0 ? (totUl / sum * 100).toFixed(1) : 0;
  const ratio = totUl > 0 ? (totDl / totUl).toFixed(1) + ':1' : '—';
  el('ch-pills').innerHTML = `
    <div class="pill"><span class="pdot" style="background:var(--blue)"></span>Download: <b>${F(totDl)}</b> (${dlPct}%)</div>
    <div class="pill"><span class="pdot" style="background:var(--purple)"></span>Upload: <b>${F(totUl)}</b> (${ulPct}%)</div>
    <div class="pill"><span>⚡</span>Ratio DL/UL: <b>${ratio}</b></div>
    <div class="pill"><span>🏆</span>Peak: <b>${F(peakVal)}</b> ${peakPeriod?'('+peakPeriod.slice(5)+')':''}</div>
  `;
  el('ch-sub').textContent = interfaceScope + ' · ' + timeZone;

  // Store chart state & render
  chartState.data = { calendar, recvD, sentD, totD, today, mx, peakPeriod };
  renderSvgChart();

  // Toggle buttons
  document.querySelectorAll('.tbtn').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.tbtn').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      chartState.mode = btn.getAttribute('data-mode');
      renderSvgChart();
    });
  });

  // Calendar coverage count
  calendar.forEach(d => {
    addRow('coverage',[d.period,''+d.observations,''+d.measuredIntervals,''+d.zeroTrafficIntervals,''+d.noActiveInterfaceObservations],[0,1,1,1,1]);
  });
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
