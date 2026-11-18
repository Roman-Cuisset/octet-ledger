using System.Globalization;
using OctetLedger.Core;

namespace OctetLedger.Tests;

[Collection("Dashboard Chromium")]
public class DashboardChartTests
{
    [Fact]
    public async Task MissingDaysBreakTrendWhileMeasuredZerosRemainVisibleInEveryMode()
    {
        long?[] received = [null, 400, 0, null, null, 0, 400, null, 200, null];
        long?[] sent = [null, 200, 0, null, null, 0, 100, null, 100, null];
        // A retained zero-byte archive has no raw collector observations; a baseline alone is not a measurement.
        int[] observations = [0, 2, 0, 0, 1, 2, 2, 0, 2, 0];
        await DashboardBrowser.VerifyAsync(Payload(received, sent, observations), """
            const hits=()=>[...document.querySelectorAll('.col-hit')];
            const center=i=>{const r=hits()[i];return +r.getAttribute('x')+(+r.getAttribute('width'))/2;};
            const baseline=Math.max(...[...document.querySelectorAll('#svg-grid line')].map(n=>+n.getAttribute('y1')));
            for(const mode of ['stacked','grouped','area']){
              document.querySelector('[data-mode="'+mode+'"]').click();
              for(const i of [0,3,4,7,9]){
                hits()[i].dispatchEvent(new MouseEvent('mouseenter'));
                check(document.querySelector('#chart-tip').textContent.includes('No measurement in selected scope'),'missing tooltip '+mode+' '+i);
                check([...document.querySelectorAll('#svg-bars rect')].some(r=>r.getAttribute('fill')==='url(#g-hatch)'&&Math.abs(+r.getAttribute('x')+(+r.getAttribute('width'))/2-center(i))<2),'missing hatch '+mode+' '+i);
              }
              for(const i of [2,5]){
                hits()[i].dispatchEvent(new MouseEvent('mouseenter'));
                check(document.querySelector('#chart-tip').textContent.includes('Measured 0 bytes traffic'),'zero tooltip '+mode+' '+i);
                const marks=[...document.querySelectorAll('#svg-bars rect,#svg-bars circle')];
                check(marks.some(r=>mode==='area'
                  ?r.tagName==='circle'&&Math.abs(+r.getAttribute('cx')-center(i))<0.1&&Math.abs(+r.getAttribute('cy')-baseline)<0.1
                  :r.tagName==='rect'&&r.getAttribute('fill')==='var(--mt)'&&Math.abs(+r.getAttribute('x')+(+r.getAttribute('width'))/2-center(i))<2),'zero mark '+mode+' '+i);
              }
            }
            const lines=[...document.querySelectorAll('#svg-bars path[fill="none"]')];
            for(const [from,to] of [[1,2],[5,6]]){
              check(lines.some(p=>Math.abs(p.getPointAtLength(0).x-center(from))<0.1&&Math.abs(p.getPointAtLength(p.getTotalLength()).x-center(to))<0.1),'missing measured segment '+from);
            }
            for(const path of lines){
              const length=path.getTotalLength();
              for(let i=0;i<=200;i++){
                const x=path.getPointAtLength(length*i/200).x;
                check((x>=center(1)-0.1&&x<=center(2)+0.1)||(x>=center(5)-0.1&&x<=center(6)+0.1),'Trend bridges a missing period');
              }
            }
            check([...document.querySelectorAll('#svg-bars circle')].some(p=>Math.abs(+p.getAttribute('cx')-center(8))<0.1),'isolated measured point lost');
            """);
    }

    [Fact]
    public async Task SmoothedCurvesStayNonNegativeAndWithinAdjacentMeasuredValues()
    {
        await DashboardBrowser.VerifyAsync(Payload([10_000, 0, 0, 10_000, 100, 8_000, 0], [0, 10_000, 0, 10, 10_000, 10_000, 0]), """
            document.querySelector('[data-mode="area"]').click();
            const baseline=Math.max(...[...document.querySelectorAll('#svg-grid line')].map(n=>+n.getAttribute('y1')));
            for(const color of ['var(--blue)','var(--purple)']){
              const vertices=[...document.querySelectorAll('#svg-bars circle')].filter(n=>n.getAttribute('fill')===color).map(n=>({x:+n.getAttribute('cx'),y:+n.getAttribute('cy')})).sort((a,b)=>a.x-b.x);
              check(vertices.length===7,'measured vertices lost');
              const line=document.querySelector('#svg-bars path[stroke="'+color+'"]');
              check(line,'measured curve missing');
              const length=line.getTotalLength();
              for(let i=0;i<=2000;i++){
                const p=line.getPointAtLength(length*i/2000);
                check(p.y<=baseline+0.001,'curve reports negative traffic');
                const next=vertices.findIndex(v=>v.x>=p.x);
                if(next>0){
                  const a=vertices[next-1],b=vertices[next];
                  check(p.y>=Math.min(a.y,b.y)-0.1&&p.y<=Math.max(a.y,b.y)+0.1,'curve overshoots measured neighbours');
                }
              }
            }
            """);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyAndEntirelyZeroPeriodsAreNotConfused(bool measured)
    {
        long?[] values = measured ? [0, 0, 0] : [null, null, null];
        await DashboardBrowser.VerifyAsync(Payload(values, values), $$"""
            const measured={{(measured ? "true" : "false")}};
            for(const mode of ['stacked','grouped','area']){
              document.querySelector('[data-mode="'+mode+'"]').click();
              for(const hit of document.querySelectorAll('.col-hit')){
                hit.dispatchEvent(new MouseEvent('mouseenter'));
                const text=document.querySelector('#chart-tip').textContent;
                check(text.includes(measured?'Measured 0 bytes traffic':'No measurement in selected scope'),'empty/zero tooltip '+mode);
              }
              const hatches=document.querySelectorAll('#svg-bars rect[fill="url(#g-hatch)"]');
              check(hatches.length===(measured?0:3),'empty/zero representation '+mode);
              if(mode==='area'&&!measured)check(!document.querySelector('#svg-bars path,#svg-bars circle'),'empty Trend invented measurements');
            }
            """);
    }

    [Fact]
    public async Task ChartIsKeyboardOperableAndStaysWithinANarrowLayout()
    {
        long?[] received = Enumerable.Range(0, 30).Select(i => i == 0 ? (long?)null : i * 1000).ToArray();
        await DashboardBrowser.VerifyAsync(Payload(received, received), """
            const tip = document.querySelector('#chart-tip');
            const hits = () => [...document.querySelectorAll('.col-hit')];
            const key = (target, value) => target.dispatchEvent(new KeyboardEvent('keydown', {key:value,bubbles:true,cancelable:true}));
            check(document.querySelector('#chart-svg title').textContent.includes('download'),'chart accessible name');
            check(hits().filter(hit=>hit.getAttribute('tabindex')==='0').length===1,'one chart tab stop');
            hits()[0].focus();
            check(hits()[0].getAttribute('aria-label').includes('No measurement')&&!tip.hidden,'focus and accessible day label');
            key(hits()[0],'ArrowRight');
            check(document.activeElement===hits()[1]&&hits()[0].getAttribute('tabindex')==='-1','arrow navigation');
            check(!tip.hidden&&tip.textContent.includes('Download'),'keyboard tooltip');
            key(hits()[1],'Escape');
            check(tip.hidden&&document.activeElement===hits()[1],'Escape dismisses without moving focus');
            key(hits()[1],'Enter');
            check(!tip.hidden,'Enter reopens the tooltip');
            document.querySelector('[data-mode="area"]').click();
            check(document.activeElement===hits()[1],'changing mode preserves chart focus');
            check(document.querySelector('[data-mode="area"]').getAttribute('aria-pressed')==='true'&&
                  document.querySelectorAll('.tbtn[aria-pressed="true"]').length===1,'selected mode announced');

            // Headless Chromium may clamp the outer window size. Keep the mobile media rules and narrow the page itself.
            check(matchMedia('(max-width: 640px)').matches,'mobile media query');
            document.documentElement.style.maxWidth='320px';
            const scroll = document.querySelector('#chart-scroll');
            const frame = document.querySelector('#ch-wrap');
            check(scroll.scrollWidth>scroll.clientWidth&&document.body.scrollWidth<=document.body.clientWidth,'chart scrolls inside the page');
            key(hits()[1],'End');
            check(document.activeElement===hits()[29]&&scroll.scrollLeft>0,'last day scrolls into view');
            const bounds=tip.getBoundingClientRect(), wrapper=frame.getBoundingClientRect();
            check(bounds.left>=wrapper.left&&bounds.right<=wrapper.right,'tooltip remains inside a narrow chart');
            key(hits()[29],'Home');
            check(document.activeElement===hits()[0],'Home returns to first day');
            document.querySelector('.scr').focus();
            hits()[0].dispatchEvent(new MouseEvent('mouseenter'));
            hits()[0].dispatchEvent(new MouseEvent('mouseleave',{relatedTarget:tip}));
            check(!tip.hidden,'pointer can enter tooltip');
            key(document.activeElement,'Escape');
            check(tip.hidden,'Escape dismisses a hover tooltip');
            """, viewportWidth: 520);
    }

    private static object Payload(long?[] received, long?[] sent, int[]? observations = null)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var now = start.AddDays(received.Length - 1);
        string Period(int index) => start.AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new
        {
            generatedAt = now,
            today = Period(received.Length - 1),
            lastCollectionUtc = now,
            nextExpectedUtc = now.AddMinutes(1),
            collectorState = "Portable/manual collection",
            watchdogInstalled = (bool?)null,
            consecutiveErrors = 0,
            interfaceScope = "Automatic: Wi-Fi",
            timeZone = "UTC",
            rows = Enumerable.Range(0, received.Length).Where(i => received[i].HasValue).Select(i =>
                new TrafficReportRow(Period(i), "wifi", "Wi-Fi", received[i]!.Value, sent[i]!.Value, 60, 1)).ToArray(),
            history = new CollectionHistory(now, [], [], start, start),
            events = Array.Empty<CollectorEvent>(),
            calendar = Enumerable.Range(0, received.Length).Select(i => new
            {
                period = Period(i),
                observations = observations?[i] ?? (received[i].HasValue ? 2 : 0),
                measuredIntervals = received[i].HasValue ? 1 : 0,
                zeroTrafficIntervals = received[i] == 0 && sent[i] == 0 ? 1 : 0,
                noActiveInterfaceObservations = 0,
                hasCollectorGap = false,
                hasInterfaceGap = false,
                hasUnknownCoverage = false
            }).ToArray()
        };
    }
}
