using OctetLedger.Cli;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public sealed class ApplicationTrafficTests
{
    [Fact]
    public void CaptureUsesEachEventsUtcHourWhenTheWindowCrossesMidnight()
    {
        var path = Path.Combine(Path.GetTempPath(), $"octetledger-apps-{Guid.NewGuid():N}.db");
        try
        {
            var midnight = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
            var capture = new ApplicationTrafficCapture();
            capture.Record(midnight.AddSeconds(-1).ToOffset(TimeSpan.FromHours(2)), "browser.exe", 100, received: true);
            capture.Record(midnight.AddMilliseconds(-100), "BROWSER.exe", 50, received: false);
            capture.Record(midnight, "browser.exe", 75, received: false);
            capture.Record(midnight.AddSeconds(1), "browser.exe", 25, received: true);
            ApplicationTrafficStore.Add(capture.GetRows(), path);

            Assert.Equal(new ApplicationTrafficRow("browser.exe", 125, 125),
                Assert.Single(ApplicationTrafficStore.ReadTop(midnight.AddHours(-1), 10, path)));
            Assert.Equal(new ApplicationTrafficRow("browser.exe", 25, 75),
                Assert.Single(ApplicationTrafficStore.ReadTop(midnight, 10, path)));
        }
        finally { File.Delete(path); }
    }
}
