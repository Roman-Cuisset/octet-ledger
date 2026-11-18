using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OctetLedger.Cli;

namespace OctetLedger.Tests;

internal static class DashboardBrowser
{
    internal static async Task VerifyAsync(object payload, string assertions, int viewportWidth = 1000)
    {
        var browserPath = FindBrowser();
        var verification = $$"""
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
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = ServeAsync(listener, verification, JsonSerializer.SerializeToUtf8Bytes(payload), cancellation.Token);
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
            $"--user-data-dir={directory}", $"--window-size={viewportWidth},900", url
        }) browser.StartInfo.ArgumentList.Add(argument);
        var started = false;
        try
        {
            browser.Start();
            started = true;
            var output = browser.StandardOutput.ReadToEndAsync(cancellation.Token);
            var error = browser.StandardError.ReadToEndAsync(cancellation.Token);
            await Task.WhenAll(browser.WaitForExitAsync(cancellation.Token), output, error).WaitAsync(cancellation.Token);
            var html = await output;
            var stderr = await error;
            Assert.True(browser.ExitCode == 0, stderr);
            const string marker = "data-test-result=\"";
            var markerStart = html.IndexOf(marker, StringComparison.Ordinal);
            var result = markerStart < 0 ? "Browser assertions did not complete. " + stderr
                : WebUtility.HtmlDecode(html[(markerStart + marker.Length)..html.IndexOf('"', markerStart + marker.Length)]);
            Assert.True(result == "pass", result);
        }
        catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Chromium did not finish dashboard verification in 60 seconds (viewport {viewportWidth}px).", exception);
        }
        finally
        {
            try
            {
                if (started && !browser.HasExited)
                {
                    browser.Kill(entireProcessTree: true);
                    await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { }
            await cancellation.CancelAsync();
            listener.Stop();
            await server;
            await DeleteProfileAsync(directory);
        }
    }

    private static async Task DeleteProfileAsync(string directory)
    {
        // A Chromium child can briefly retain profile handles after the main process exits.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(directory, recursive: true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
        }
    }

    private static async Task ServeAsync(HttpListener listener, string verification, byte[] payload, CancellationToken cancellationToken)
    {
        var html = Encoding.UTF8.GetString(DashboardAssets.Files["/"].Bytes)
            .Replace("</body>", "<script src=\"/verify.js\" defer></script></body>", StringComparison.Ordinal);
        var files = new Dictionary<string, (byte[] Bytes, string ContentType)>(DashboardAssets.Files)
        {
            ["/"] = (Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8"),
            ["/api/data"] = (payload, "application/json; charset=utf-8"),
            ["/verify.js"] = (Encoding.UTF8.GetBytes(verification), "text/javascript; charset=utf-8")
        };
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
                using var response = context.Response;
                DashboardHttp.SetHeaders(response);
                if (!files.TryGetValue(context.Request.Url?.AbsolutePath ?? "", out var asset))
                {
                    response.StatusCode = 404;
                    continue;
                }
                response.ContentType = asset.ContentType;
                response.ContentLength64 = asset.Bytes.Length;
                await response.OutputStream.WriteAsync(asset.Bytes, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static string FindBrowser()
    {
        var configured = Environment.GetEnvironmentVariable("OCTETLEDGER_TEST_BROWSER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var path in new[]
        {
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")
        })
        {
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Dashboard tests require Google Chrome, Microsoft Edge, or OCTETLEDGER_TEST_BROWSER pointing to a Chromium browser.");
    }
}
