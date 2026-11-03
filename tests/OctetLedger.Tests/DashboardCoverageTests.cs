using System.Globalization;
using OctetLedger.Cli;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public class DashboardCoverageTests
{
    [Theory]
    [InlineData(null, "unknown_collection_gap", true, false, false)]
    [InlineData(null, "clock_not_advanced", true, false, false)]
    [InlineData(null, "no_active_interfaces", false, true, false)]
    [InlineData(null, "unknown_coverage", false, false, true)]
    [InlineData("WIFI", "interface_not_up", false, true, false)]
    [InlineData("wifi", "counter_reset", false, true, false)]
    [InlineData("wifi", "clock_not_advanced", false, true, false)]
    [InlineData("virtual", "interface_not_up", false, false, false)]
    [InlineData("virtual", "clock_not_advanced", false, false, false)]
    public void GapCauseAndSelectedInterfaceDetermineTheIndicator(string? id, string reason, bool collector, bool networkInterface, bool unknown)
    {
        var gap = new CollectionGap(Start.AddHours(1), Start.AddHours(2), reason, id, id);
        var result = DashboardCommand.GetGapIndicators([gap], new HashSet<string>(["wifi"], StringComparer.OrdinalIgnoreCase),
            Start, Start.AddDays(1), Start.AddDays(2));
        Assert.Equal((collector, networkInterface, unknown), result);
    }

    [Theory]
    [InlineData(-60, 0, false)]
    [InlineData(-60, 60, true)]
    [InlineData(60, 120, true)]
    [InlineData(1440, 1500, false)]
    [InlineData(0, 0, true)]
    [InlineData(1440, 1440, false)]
    public void IndicatorsRespectHalfOpenDayBoundariesAndInstantaneousEvents(int startMinute, int endMinute, bool expected)
    {
        var gap = new CollectionGap(Start.AddMinutes(startMinute), Start.AddMinutes(endMinute), "clock_not_advanced", null, null);
        var result = DashboardCommand.GetGapIndicators([gap], new HashSet<string>(), Start, Start.AddDays(1), Start.AddDays(2));
        Assert.Equal(expected, result.Collector);
    }

    [Fact]
    public void OngoingGapOpenedNowIsVisibleButFutureEventsAreNot()
    {
        var now = Start.AddHours(12);
        var result = DashboardCommand.GetGapIndicators([
            new CollectionGap(now, null, "interface_not_up", "wifi", "Wi-Fi"),
            new CollectionGap(now.AddHours(1), null, "unknown_collection_gap", null, null)
        ], new HashSet<string>(["wifi"]), Start, Start.AddDays(1), now);
        Assert.Equal((false, true, false), result);
    }

    [Theory]
    [InlineData(null, "virtual", false)]
    [InlineData(null, "ethernet", true)]
    [InlineData("wifi", "ethernet", false)]
    [InlineData("virtual", "virtual", true)]
    public void InterruptionScopeFollowsPhysicalFallbackAndExplicitPreference(string? preferred, string interrupted, bool expected)
    {
        TrafficBucket[] buckets = [
            new("wifi", "Wi-Fi", Start, 100, 0, InterfaceDescription: "Intel Wi-Fi", InterfaceType: "Wireless80211"),
            new("ethernet", "Ethernet", Start, 200, 0, InterfaceDescription: "Intel Ethernet", InterfaceType: "Ethernet"),
            new("virtual", "Connexion au réseau local* 8", Start, 0, 0, InterfaceDescription: "Microsoft Wi-Fi Direct Virtual Adapter", InterfaceType: "Wireless80211")
        ];
        var selectedIds = TrafficReport.SelectInterface(buckets, null, preferred)
            .Select(bucket => bucket.InterfaceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = DashboardCommand.GetGapIndicators([
            new CollectionGap(Start, null, "interface_not_up", interrupted, interrupted)
        ], selectedIds, Start, Start.AddDays(1), Start.AddHours(12));
        Assert.Equal(expected, result.Interface);
        Assert.False(result.Collector);
    }

    [Fact]
    public async Task BrowserDistinguishesCollectorInterfaceAndUnknownCoverageWithoutVirtualFalseAlarms()
    {
        var now = Start.AddDays(5);
        CollectionGap[] gaps = [
            new(Start, Start.AddHours(1), "unknown_coverage", null, null),
            new(Start.AddDays(1), Start.AddDays(1).AddHours(1), "interface_not_up", "virtual", "Virtual adapter"),
            new(Start.AddDays(2), Start.AddDays(2).AddHours(1), "counter_reset", "wifi", "Wi-Fi"),
            new(Start.AddDays(3), Start.AddDays(3).AddHours(1), "unknown_collection_gap", null, null),
            new(Start.AddDays(3), Start.AddDays(3).AddHours(1), "interface_not_observed", "ethernet", "Ethernet")
        ];
        var selectedIds = new HashSet<string>(["wifi", "ethernet"]);
        string Period(int i) => Start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var calendar = Enumerable.Range(0, 5).Select(i =>
        {
            var indicators = DashboardCommand.GetGapIndicators(gaps, selectedIds, Start.AddDays(i), Start.AddDays(i + 1), now);
            return new
            {
                period = Period(i),
                observations = 2,
                measuredIntervals = 1,
                zeroTrafficIntervals = 0,
                noActiveInterfaceObservations = 0,
                hasCollectorGap = indicators.Collector,
                hasInterfaceGap = indicators.Interface,
                hasUnknownCoverage = indicators.UnknownCoverage
            };
        }).ToArray();
        var payload = new
        {
            generatedAt = now,
            today = Period(4),
            lastCollectionUtc = now,
            nextExpectedUtc = now.AddMinutes(1),
            collectorState = "Portable/manual collection",
            watchdogInstalled = (bool?)null,
            consecutiveErrors = 0,
            interfaceScope = "Automatic: Wi-Fi, Ethernet",
            timeZone = "UTC",
            calendar,
            rows = Enumerable.Range(0, 5).Select(i => new TrafficReportRow(Period(i), "wifi", "Wi-Fi", 100, 50, 60, 1)).ToArray(),
            history = new CollectionHistory(now, [], gaps, Start, Start),
            events = Array.Empty<CollectorEvent>()
        };
        await DashboardBrowser.VerifyAsync(payload, """
            for(const mode of ['stacked','grouped','area']){
              document.querySelector('[data-mode="'+mode+'"]').click();
              const hits=[...document.querySelectorAll('.col-hit')];
              const tooltip=i=>{hits[i].dispatchEvent(new MouseEvent('mouseenter'));return document.querySelector('#chart-tip').textContent;};
              check(tooltip(0).includes('Coverage unknown')&&!document.querySelector('#chart-tip .tip-warn'),'unknown coverage blamed on collection');
              check(!tooltip(1).includes('interruption'),'excluded virtual adapter caused an alarm');
              check(tooltip(2).includes('Interface interruption')&&!tooltip(2).includes('Collection interruption'),'selected adapter blamed on collector');
              check(tooltip(3).includes('Collection interruption')&&tooltip(3).includes('Interface interruption'),'simultaneous causes lost');
              check(!tooltip(4).includes('interruption'),'healthy day has an alarm');
              const markers=[...document.querySelectorAll('#svg-bars circle')].filter(n=>n.querySelector('title'));
              const filled=markers.filter(n=>n.getAttribute('fill')==='var(--yellow)');
              const hollow=markers.filter(n=>n.getAttribute('fill')==='none'&&n.getAttribute('stroke')==='var(--yellow)');
              check(filled.length===1&&hollow.length===2,'collector and interface markers not visually distinct');
              check(document.querySelector('.lgd').textContent.includes('Collection interruption')&&document.querySelector('.lgd').textContent.includes('Interface interruption'),'marker meanings missing from legend');
            }
            """);
    }

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
