const el=id=>document.getElementById(id),V=(r,n)=>r[n]??r[n[0].toLowerCase()+n.slice(1)];
const F=n=>{if(n<=0||isNaN(n))return'0 B';const u=['B','KiB','MiB','GiB','TiB'];let i=0;while(n>=1024&&i<u.length-1){n/=1024;i++}return n.toFixed(n>=100?0:n>=10?1:2)+' '+u[i]};
const T=n=>n?new Date(n).toLocaleString():'—';
const P=p=>({'raw_intervals':'Counter intervals','daily_archives':'Daily archives','mixed':'Mixed'}[p]??'Unknown');
const R=r=>({'unknown_collection_gap':'Unknown cause','interface_not_observed':'Not observed','interface_not_up':'Interface down',
  'counter_reset':'Counter reset','clock_not_advanced':'Clock stalled','no_active_interfaces':'No active interface',
  'unknown_coverage':'Unknown coverage'}[r]??r.replaceAll('_',' '));

function addRow(id,txts,nums){const tr=document.createElement('tr');txts.forEach((t,i)=>{
  const td=document.createElement('td');td.textContent=t;if(nums[i])td.className='n';tr.append(td)});el(id).append(tr)}

function addTimelineItem(target,start,end,description,className=''){
  const li=document.createElement('li'),time=document.createElement('span'),detail=document.createElement('span');
  li.className=className;time.className='t';time.textContent=T(start);
  detail.className='r';detail.textContent=description;
  li.append(time,document.createTextNode(' → '+end),document.createElement('br'),detail);
  el(target).append(li);
}

const chartState = { mode: 'stacked', data: null, focusIndex: 0, tipTarget: null };
const chartTip = el('chart-tip');

function dayDetails(d) {
  const { recvD, sentD, totD, today, peakPeriod } = chartState.data;
  const received = recvD.get(d.period) ?? 0, sent = sentD.get(d.period) ?? 0;
  const total = received + sent;
  const measurement = !totD.has(d.period) ? 'No measurement in selected scope' :
    total === 0 ? 'Measured 0 bytes traffic' : null;
  const badges = [];
  if (d.period === today) badges.push('Today');
  if (d.period === peakPeriod && total > 0) badges.push('Peak');
  const observations = `${d.observations} collector observation(s)`;
  const warnings = [];
  if (d.hasCollectorGap) warnings.push('Collection interruption recorded');
  if (d.hasInterfaceGap) warnings.push('Interface interruption in selected scope');
  const coverage = d.hasUnknownCoverage ? 'Coverage unknown for part of this day' : null;
  return { received, sent, total, measurement, badges, observations, warnings, coverage };
}

function chartTipElement(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function showChartTip(hit, day, details) {
  const { received, sent, total, measurement, badges, observations, warnings, coverage } = details;
  const header = chartTipElement('div', 'tip-hdr');
  header.append(chartTipElement('span', '', day.period));
  for (const badge of badges) header.append(chartTipElement('span', 'tip-badge ' + badge.toLowerCase(), badge));
  chartTip.replaceChildren(header);
  if (measurement) {
    chartTip.append(chartTipElement('div', 'tip-row tip-muted', measurement));
  } else {
    for (const [label, amount, marker] of [['Download', received, 'download-marker'], ['Upload', sent, 'upload-marker']]) {
      const row = chartTipElement('div', 'tip-row');
      const labelNode = chartTipElement('span', '');
      labelNode.append(chartTipElement('i', 'c-dot ' + marker), label);
      row.append(labelNode, chartTipElement('b', '', F(amount)),
        chartTipElement('span', 'tip-pct', Math.round(amount / total * 100) + '%'));
      chartTip.append(row);
    }
    const sum = chartTipElement('div', 'tip-row total');
    sum.append(chartTipElement('span', '', 'Total'), chartTipElement('b', '', F(total)));
    chartTip.append(sum);
  }
  chartTip.append(chartTipElement('div', 'tip-row tip-muted tip-observations', observations));
  for (const warning of warnings) chartTip.append(chartTipElement('div', 'tip-warn', '⚠️ ' + warning));
  if (coverage) chartTip.append(chartTipElement('div', 'tip-row tip-muted', coverage));
  chartState.tipTarget = hit;
  chartTip.hidden = false;
  positionChartTip();
}

function hideChartTip() {
  chartState.tipTarget = null;
  chartTip.hidden = true;
}

function positionChartTip() {
  if (!chartState.tipTarget) return;
  const wrap = el('ch-wrap').getBoundingClientRect();
  const hit = chartState.tipTarget.getBoundingClientRect();
  const width = chartTip.offsetWidth;
  const center = (hit.left + hit.right) / 2 - wrap.left;
  chartTip.style.left = Math.max(4, Math.min(center - width / 2, wrap.width - width - 4)) + 'px';
}

function setupChartDays(hits, calendar) {
  hits.forEach((hit, index) => {
    const day = calendar[index], details = dayDetails(day);
    const { received, sent, total, measurement, badges, observations, warnings, coverage } = details;
    hit.setAttribute('aria-label', [day.period, ...badges,
      measurement ?? `Download ${F(received)}, upload ${F(sent)}, total ${F(total)}`,
      observations, ...warnings, coverage].filter(Boolean).join('. '));

    const show = () => showChartTip(hit, day, details);
    const hideIfOutside = event => {
      if (chartState.tipTarget === hit && !chartTip.contains(event.relatedTarget) &&
          document.activeElement !== hit && !hit.matches(':hover') && !chartTip.matches(':hover')) hideChartTip();
    };
    hit.addEventListener('mouseenter', show);
    hit.addEventListener('mouseleave', hideIfOutside);
    hit.addEventListener('focus', () => {
      hits[chartState.focusIndex]?.setAttribute('tabindex', '-1');
      hit.setAttribute('tabindex', '0');
      chartState.focusIndex = index;
      show();
    });
    hit.addEventListener('blur', hideIfOutside);
    hit.addEventListener('click', () => {
      hit.focus();
      show();
    });
    hit.addEventListener('keydown', event => {
      let next = index;
      switch (event.key) {
        case 'ArrowLeft': next = Math.max(0, index - 1); break;
        case 'ArrowRight': next = Math.min(hits.length - 1, index + 1); break;
        case 'Home': next = 0; break;
        case 'End': next = hits.length - 1; break;
        case 'Enter': case ' ': show(); break;
        default: return;
      }
      event.preventDefault();
      if (next !== index) hits[next].focus();
    });
  });
}

chartTip.addEventListener('mouseleave', () => {
  if (chartState.tipTarget !== document.activeElement) hideChartTip();
});
el('chart-scroll').addEventListener('scroll', positionChartTip);
window.addEventListener('resize', positionChartTip);
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && !chartTip.hidden) hideChartTip();
});

function renderSvgChart() {
  if (!chartState.data) return;
  const { calendar, recvD, sentD, totD, today, mx } = chartState.data;
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
    gridHtml += `<text class="axis-label" x="${leftX - 8}" y="${y + 3.5}" text-anchor="end" fill="var(--mt)" font-size="10" font-family="system-ui">${F(val)}</text>`;
  }
  gridG.innerHTML = gridHtml;

  // Data representation
  const barsG = el('svg-bars');
  const hitsG = el('svg-hits');
  let barsHtml = '', hitsHtml = '';

  if (mode === 'area') {
    const dlPts = [], ulPts = [];
    function buildSpline(pts) {
      if (!pts.length) return { line: '', area: '' };
      let d = `M ${pts[0].x} ${pts[0].y}`;
      for (let i = 0; i < pts.length - 1; i++) {
        const p0 = pts[i === 0 ? 0 : i - 1], p1 = pts[i], p2 = pts[i + 1], p3 = pts[i + 2 >= pts.length ? pts.length - 1 : i + 2];
        // A cubic Bézier stays in its control-point hull; constrain it to the two measured values.
        const lo = Math.min(p1.y, p2.y), hi = Math.max(p1.y, p2.y);
        const bounded = y => Math.max(lo, Math.min(hi, y));
        const cp1x = p1.x + (p2.x - p0.x) / 6, cp1y = bounded(p1.y + (p2.y - p0.y) / 6);
        const cp2x = p2.x - (p3.x - p1.x) / 6, cp2y = bounded(p2.y - (p3.y - p1.y) / 6);
        d += ` C ${cp1x.toFixed(1)} ${cp1y.toFixed(1)}, ${cp2x.toFixed(1)} ${cp2y.toFixed(1)}, ${p2.x.toFixed(1)} ${p2.y.toFixed(1)}`;
      }
      return { line: d, area: d + ` L ${pts[pts.length - 1].x} ${botY} L ${pts[0].x} ${botY} Z` };
    }
    function drawSegment() {
      if (dlPts.length > 1) {
        const dlS = buildSpline(dlPts), ulS = buildSpline(ulPts);
        barsHtml += `<path d="${dlS.area}" fill="url(#g-dl-area)"/>`;
        barsHtml += `<path d="${dlS.line}" fill="none" stroke="var(--blue)" stroke-width="2.5"/>`;
        barsHtml += `<path d="${ulS.area}" fill="url(#g-ul-area)"/>`;
        barsHtml += `<path d="${ulS.line}" fill="none" stroke="var(--purple)" stroke-width="2"/>`;
      }
      dlPts.forEach(p => { barsHtml += `<circle cx="${p.x}" cy="${p.y}" r="3" fill="var(--blue)"/>`; });
      ulPts.forEach(p => { barsHtml += `<circle cx="${p.x}" cy="${p.y}" r="2.5" fill="var(--purple)"/>`; });
      dlPts.length = ulPts.length = 0;
    }
    calendar.forEach((d, i) => {
      if (!totD.has(d.period)) { drawSegment(); return; }
      const cx = leftX + i * step + step / 2;
      dlPts.push({ x: cx, y: botY - recvD.get(d.period) / mx * chartH });
      ulPts.push({ x: cx, y: botY - sentD.get(d.period) / mx * chartH });
    });
    drawSegment();
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
        if (rH > 0) {
          const rxB = sH > 0 ? 0 : 3;
          barsHtml += `<rect x="${bx}" y="${botY - rH}" width="${bw}" height="${rH}" fill="url(#g-dl)" rx="${rxB}" opacity="${isToday?0.75:1}"/>`;
        }
        if (sH > 0) {
          barsHtml += `<rect x="${bx}" y="${botY - rH - sH}" width="${bw}" height="${sH}" fill="url(#g-ul)" rx="3" opacity="${isToday?0.75:1}"/>`;
        }
      } else if (!has) {
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
      } else if (!has) {
        barsHtml += `<rect x="${cx - bw}" y="${botY - 6}" width="${bw * 2 + 2}" height="6" fill="url(#g-hatch)" rx="2" opacity="0.6"/>`;
      } else {
        barsHtml += `<rect x="${cx - bw}" y="${botY - 2}" width="${bw * 2 + 2}" height="2" fill="var(--mt)" rx="1" opacity="0.5"/>`;
      }
    } else if (mode === 'area' && !has) {
      const bw = Math.min(18, Math.max(8, step - 6));
      barsHtml += `<rect x="${cx - bw / 2}" y="${botY - 6}" width="${bw}" height="6" fill="url(#g-hatch)" rx="2" opacity="0.6"/>`;
    }

    // Collector-wide interruptions and selected-interface interruptions have different markers.
    if (d.hasCollectorGap) {
      barsHtml += `<circle cx="${cx - (d.hasInterfaceGap ? 3 : 0)}" cy="${botY + 8}" r="2.5" fill="var(--yellow)"><title>Collection interruption</title></circle>`;
    }
    if (d.hasInterfaceGap) {
      barsHtml += `<circle cx="${cx + (d.hasCollectorGap ? 3 : 0)}" cy="${botY + 8}" r="2.5" fill="none" stroke="var(--yellow)" stroke-width="1.5"><title>Interface interruption in selected scope</title></circle>`;
    }

    // Date label
    const dayNum = String(Number(p.slice(8))).padStart(2, '0');
    const lblColor = isToday ? 'var(--blue)' : 'var(--mt)';
    const lblWeight = isToday ? '700' : '400';
    barsHtml += `<text class="axis-label" x="${cx}" y="${botY + 22}" text-anchor="middle" fill="${lblColor}" font-weight="${lblWeight}" font-size="9.5" font-family="system-ui">${dayNum}</text>`;

    // Interactive hover zone
    hitsHtml += `<rect x="${leftX + i * step}" y="${topY - 6}" width="${step}" height="${chartH + 34}" class="col-hit" role="button" tabindex="${i === chartState.focusIndex ? 0 : -1}"/>`;
  });

  const restoreFocus = hitsG.contains(document.activeElement);
  hideChartTip();
  barsG.innerHTML = barsHtml;
  hitsG.innerHTML = hitsHtml;
  const hits = hitsG.querySelectorAll('.col-hit');
  setupChartDays(hits, calendar);
  if (restoreFocus) hits[chartState.focusIndex]?.focus();
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
    <div class="pill"><span class="pdot download-marker"></span>Download: <b>${F(totDl)}</b> (${dlPct}%)</div>
    <div class="pill"><span class="pdot upload-marker"></span>Upload: <b>${F(totUl)}</b> (${ulPct}%)</div>
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
      document.querySelectorAll('.tbtn').forEach(button => {
        button.classList.toggle('active', button === btn);
        button.setAttribute('aria-pressed', String(button === btn));
      });
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
    const nm=V(g,'InterfaceName');
    addTimelineItem('gaps',V(g,'StartUtc'),V(g,'EndUtc')?T(V(g,'EndUtc')):'ongoing',
      R(V(g,'Reason'))+(nm?' · '+nm:''),'w');
  }
  if(!gaps.length){const li=document.createElement('li');li.textContent='No interruption in retained detail.';el('gaps').append(li)}
  el('gcnt').textContent=gaps.length;

  // Events
  const ev=events.slice(-20);
  for(const e of ev){
    addTimelineItem('events',V(e,'TimestampUtc'),T(V(e,'LastTimestampUtc')),
      V(e,'Kind').replaceAll('_',' ')+' ×'+V(e,'Count'));
  }
  if(!ev.length){const li=document.createElement('li');li.textContent='No events in this period.';el('events').append(li)}
  el('ecnt').textContent=ev.length;

  el('meta').textContent=(rows.length?'':'No measured traffic. ')+interfaceScope+' \u00B7 '+timeZone;
  el('ft').textContent='OctetLedger \u00B7 '+timeZone+' \u00B7 Generated '+T(generatedAt);
}).catch(e=>{el('meta').textContent='Error: '+e.message;el('state').textContent='Unavailable';el('dot').className='dot err'});
