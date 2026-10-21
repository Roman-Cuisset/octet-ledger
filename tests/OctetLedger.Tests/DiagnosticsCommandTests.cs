using System.Text;
using System.Text.Json;
using OctetLedger.Cli;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public class DiagnosticsCommandTests
{
    [Fact]
    public void ReportWhitelistsHealthAndErrorTypesWithoutPrivateDetails()
    {
        const string secret = "private-host-user-adapter-path-exception-secret";
        var now = DateTimeOffset.Parse("2026-10-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var collector = new CollectorTaskStatus(true, "Running, retrying after errors", secret)
        {
            WatchdogInstalled = true,
            ConsecutiveErrors = 4
        };
        var report = DiagnosticsCommand.SerializeDiagnosticReport(new Version(1, 2, 3), now,
            collector, portable: false, stopRequested: false,
            new DatabaseCheckResult(false, [secret]),
            [new CollectorEvent(now, "error", 3, now.AddSeconds(2), "IOException"),
             new CollectorEvent(now, "error", 1, now, secret),
             new CollectorEvent(now, secret, 1, now, secret),
             new CollectorEvent(now.AddSeconds(3), "recovery", 1, now.AddSeconds(3))]);

        Assert.DoesNotContain(secret, report);
        Assert.DoesNotContain(AppDataPaths.DataDirectory, report);
        Assert.DoesNotContain(Environment.MachineName + "\\", report);
        using var json = JsonDocument.Parse(report);
        var root = json.RootElement;
        Assert.Equal("1.2.3", root.GetProperty("ApplicationVersion").GetString());
        Assert.Equal("failed", root.GetProperty("Database").GetProperty("Integrity").GetString());
        Assert.Equal(TrafficStore.CurrentSchemaVersion, root.GetProperty("Database").GetProperty("SupportedSchemaVersion").GetInt32());
        Assert.Equal(4, root.GetProperty("Collection").GetProperty("ConsecutiveErrors").GetInt32());
        Assert.True(root.GetProperty("Collection").GetProperty("WatchdogInstalled").GetBoolean());
        var errors = root.GetProperty("EventHistory").GetProperty("ErrorCounts").EnumerateArray().ToArray();
        Assert.Contains(errors, item => item.GetProperty("Type").GetString() == "IOException" && item.GetProperty("Count").GetInt64() == 3);
        Assert.Contains(errors, item => item.GetProperty("Type").GetString() == "OtherError" && item.GetProperty("Count").GetInt64() == 1);
        Assert.All(root.GetProperty("Privacy").EnumerateObject(), property => Assert.False(property.Value.GetBoolean()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewIsExactlyTheExportPayloadForTheSameSnapshot(bool explicitPreview)
    {
        var report = DiagnosticsCommand.SerializeDiagnosticReport(new Version(1, 0), DateTimeOffset.UnixEpoch,
            new CollectorTaskStatus(false, "Portable/manual collection"), true, false, null, []);
        var preview = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, DiagnosticsCommand.RunDiagnostics(explicitPreview ? ["preview"] : [], () => report, preview, error));
        Assert.Equal(report, preview.ToString());
        Assert.Equal("", error.ToString());
        var directory = Path.Combine(Path.GetTempPath(), $"octetledger-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.json");
            Assert.Equal(0, DiagnosticsCommand.RunDiagnostics(["export", path], () => report, new StringWriter(), error));
            Assert.Equal(Encoding.UTF8.GetBytes(preview.ToString()), File.ReadAllBytes(path));
            Assert.Equal("", error.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExportRefusesOverwriteWithoutDisclosingTheDestinationOrExceptionMessage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"private-diagnostic-destination-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "existing contents");
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var result = DiagnosticsCommand.RunDiagnostics(["export", path], () => "{}\n", output, error);

            Assert.Equal(1, result);
            Assert.Equal("existing contents", File.ReadAllText(path));
            Assert.Equal("", output.ToString());
            Assert.DoesNotContain(path, error.ToString());
            Assert.DoesNotContain(Path.GetFileName(path), error.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(@"\\private-server\share\report.json")]
    [InlineData("//private-server/share/report.json")]
    public void NetworkDestinationsAreRejectedBeforeTakingASnapshot(string path)
    {
        var captured = false;
        var result = DiagnosticsCommand.RunDiagnostics(["export", path], () =>
        {
            captured = true;
            return "{}\n";
        }, new StringWriter(), new StringWriter());

        Assert.Equal(2, result);
        Assert.False(captured);
    }

    [Theory]
    [InlineData("--include-logs")]
    [InlineData("upload")]
    [InlineData("export")]
    public void UnsupportedArgumentsDoNotCaptureData(string argument)
    {
        var captured = false;
        var result = DiagnosticsCommand.RunDiagnostics([argument], () =>
        {
            captured = true;
            return "{}\n";
        }, new StringWriter(), new StringWriter());

        Assert.Equal(2, result);
        Assert.False(captured);
    }
}
