using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OctetLedger.Cli;

namespace OctetLedger.Tests;

internal static class DashboardBrowser
{
    internal static async Task VerifyAsync(object payload, string assertions)
    {
        var browserPath = FindBrowser();
        var verification = $$"""
            <script>
            function verifyDashboard(){
              if(!chartState.data){setTimeout(verifyDashboard,20);return;}
              // Let resource/error handlers run before checking hostile data.
              setTimeout(()=>{
                try{
                  const check=(condition,message)=>{if(!condition)throw new Error(message);};
                  {{assertions}}
                  document.body.dataset.testResult='pass';
                }catch(error){document.body.dataset.testResult='fail: '+error.message;}
              },100);
            }
            verifyDashboard();
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
        var server = ServeAsync(listener, DashboardCommand.Html + verification, JsonSerializer.Serialize(payload), cancellation.Token);
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
            const string marker = "data-test-result=\"";
            var markerStart = html.IndexOf(marker, StringComparison.Ordinal);
            var result = markerStart < 0 ? "Browser assertions did not complete. " + stderr
                : WebUtility.HtmlDecode(html[(markerStart + marker.Length)..html.IndexOf('"', markerStart + marker.Length)]);
            Assert.True(result == "pass", result);
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
        throw new FileNotFoundException("Dashboard tests require Microsoft Edge or OCTETLEDGER_TEST_BROWSER pointing to a Chromium browser.");
    }
}
