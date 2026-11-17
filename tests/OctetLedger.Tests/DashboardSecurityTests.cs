using System.Text.Json;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public class DashboardSecurityTests
{
    [Fact]
    public async Task LocalDataIsDisplayedLiterallyWithoutCreatingElementsOrExecutingJavaScript()
    {
        const string name = "<img src=/name onerror=document.body.dataset.injected=1>";
        const string reason = "<svg onload=document.body.dataset.injected=2>";
        const string kind = "<img src=/kind onerror=document.body.dataset.injected=3>";
        var now = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var payload = new
        {
            generatedAt = now,
            today = "2026-01-02",
            lastCollectionUtc = now,
            nextExpectedUtc = now.AddMinutes(1),
            collectorState = "Portable/manual collection",
            watchdogInstalled = (bool?)null,
            consecutiveErrors = 0,
            interfaceScope = name,
            timeZone = "UTC",
            rows = new[] { new TrafficReportRow("2026-01-02", "wifi", name, 100, 50, 1, 2) },
            history = new CollectionHistory(now, [], [new CollectionGap(now.AddMinutes(-2), now, reason, "wifi", name)], now, now),
            events = new[] { new CollectorEvent(now, kind, 2, now) },
            calendar = new[] { new { period = "2026-01-02", observations = 2, measuredIntervals = 1, zeroTrafficIntervals = 0, noActiveInterfaceObservations = 0, hasCollectorGap = true, hasInterfaceGap = true, hasUnknownCoverage = false } }
        };
        var expected = JsonSerializer.Serialize(new { name, reason, kind });
        await DashboardBrowser.VerifyAsync(payload, $$"""
            const expected={{expected}};
            check(document.querySelector('#rows td:nth-child(2)').textContent===expected.name,'table text');
            check(document.querySelector('#gaps .r').textContent===expected.reason+' · '+expected.name,'gap text');
            check(document.querySelector('#events .r').textContent===expected.kind+' ×2','event text');
            check(document.querySelector('#meta').textContent.includes(expected.name),'scope text');
            check(!document.querySelector('#gaps img,#gaps svg,#events img,#events svg'),'injected element');
            check(!document.body.dataset.injected,'executed handler');
            check(getComputedStyle(document.body).backgroundColor==='rgb(13, 17, 23)','embedded stylesheet blocked');
            const inline = document.createElement('script');
            inline.textContent = 'document.body.dataset.inlineScriptExecuted = 1';
            document.body.append(inline);
            check(!document.body.dataset.inlineScriptExecuted,'CSP allowed inline script');
            """);
    }
}
