using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OctetLedger.Cli;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public class DashboardSecurityTests
{
    [Fact]
    public async Task LocalDataIsDisplayedLiterallyWithoutCreatingElementsOrExecutingJavaScript()
    {
        var browserPath = FindBrowser();
        const string name = "<img src=/name onerror=document.body.dataset.injected=1>";
        const string reason = "<svg onload=document.body.dataset.injected=2>";
        const string kind = "<img src=/kind onerror=document.body.dataset.injected=3>";
        var now = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var payload = JsonSerializer.Serialize(new
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
            calendar = new[] { new { period = "2026-01-02", observations = 2, measuredIntervals = 1, zeroTrafficIntervals = 0, noActiveInterfaceObservations = 0, hasGap = true } }
        });
        var expected = JsonSerializer.Serialize(new { name, reason, kind });
        var verification = $$"""
            <script>
            const expected={{expected}};
            function verifySecurity(){
              if(!document.querySelector('#events li')){setTimeout(verifySecurity,20);return;}
              // Allow injected resource/error handlers to run before checking the DOM.
              setTimeout(()=>{
                const failures=[];
                if(document.querySelector('#rows td:nth-child(2)').textContent!==expected.name)failures.push('table text');
                if(document.querySelector('#gaps .r').textContent!==expected.reason+' · '+expected.name)failures.push('gap text');
                if(document.querySelector('#events .r').textContent!==expected.kind+' ×2')failures.push('event text');
                if(document.querySelector('#meta').textContent.indexOf(expected.name)<0)failures.push('scope text');
                if(document.querySelector('#gaps img,#gaps svg,#events img,#events svg'))failures.push('injected element');
                if(document.body.dataset.injected)failures.push('executed handler');
                for(const mode of ['stacked','grouped','area']){
                  document.querySelector('[data-mode="'+mode+'"]').click();
                  if(!document.querySelector('#svg-bars rect,#svg-bars path'))failures.push('chart '+mode);
                }
                document.body.dataset.securityResult=failures.length?'fail: '+failures.join(', '):'pass';
              },100);
            }
            verifySecurity();
            </script>
            """;
        var directory = Path.Combine(Path.GetTempPath(), $"octetledger-dashboard-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var listener = new HttpListener();
        var url = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(url);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var server = ServeAsync(listener, DashboardCommand.Html + verification, payload, cancellation.Token);
        using var browser = new Process
        {
            StartInfo = new ProcessStartInfo(browserPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in new[]
        {
            "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--disable-background-networking", "--disable-extensions", "--dump-dom", "--virtual-time-budget=5000",
            $"--user-data-dir={directory}", url
        }) browser.StartInfo.ArgumentList.Add(argument);
        var started = false;
        try
        {
            browser.Start();
            started = true;
            var output = browser.StandardOutput.ReadToEndAsync(cancellation.Token);
            var error = browser.StandardError.ReadToEndAsync(cancellation.Token);
            await browser.WaitForExitAsync(cancellation.Token);
            var html = await output;
            var stderr = await error;
            Assert.True(browser.ExitCode == 0, stderr);
            Assert.Contains("data-security-result=\"pass\"", html, StringComparison.Ordinal);
        }
        finally
        {
            if (started && !browser.HasExited)
            {
                browser.Kill(entireProcessTree: true);
                await browser.WaitForExitAsync(CancellationToken.None);
            }
            await cancellation.CancelAsync();
            listener.Stop();
            await server;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ServeAsync(HttpListener listener, string html, string payload, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
                using var response = context.Response;
                var api = context.Request.Url?.AbsolutePath == "/api/data";
                response.ContentType = api ? "application/json; charset=utf-8" : "text/html; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes(api ? payload : html);
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static string FindBrowser()
    {
        var configured = Environment.GetEnvironmentVariable("OCTETLEDGER_TEST_BROWSER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        if (File.Exists(edge)) return edge;
        edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe");
        if (File.Exists(edge)) return edge;
        throw new FileNotFoundException("Dashboard security tests require Microsoft Edge or OCTETLEDGER_TEST_BROWSER pointing to a Chromium browser.");
    }
}
