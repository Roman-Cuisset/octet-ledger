using System.Net;
using System.Net.Sockets;
using System.Text;
using OctetLedger.Cli;

namespace OctetLedger.Tests;

public sealed class DashboardHttpTests
{
    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/dashboard.css", "text/css")]
    [InlineData("/dashboard.js", "text/javascript")]
    [InlineData("/api/data", "application/json")]
    public async Task LoopbackGetAndHeadServeTheCorrectRepresentation(string path, string mediaType)
    {
        await using var dashboard = new Fixture();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", dashboard.Address.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var response = await dashboard.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("unsafe-inline", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(body);

        using var head = new HttpRequestMessage(HttpMethod.Head, path);
        using var headers = await dashboard.Client.SendAsync(head);
        Assert.Equal(HttpStatusCode.OK, headers.StatusCode);
        Assert.Equal(body.LongLength, headers.Content.Headers.ContentLength);
        Assert.Empty(await headers.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("OPTIONS")]
    [InlineData("PUT")]
    public async Task UnsupportedMethodsNeverLoadData(string method)
    {
        await using var dashboard = new Fixture();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/data");
        using var response = await dashboard.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["GET", "HEAD"], response.Content.Headers.Allow);
        Assert.Equal(0, dashboard.DataReads);
    }

    [Theory]
    [InlineData("Origin", "http://attacker.invalid")]
    [InlineData("Origin", "null")]
    [InlineData("Sec-Fetch-Site", "same-site")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    public async Task ForeignBrowserRequestsAreRejectedBeforeDataAccess(string header, string value)
    {
        await using var dashboard = new Fixture();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/data");
        request.Headers.Add(header, value);
        using var response = await dashboard.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, dashboard.DataReads);
    }

    [Fact]
    public async Task DnsAliasesAndAbsoluteRequestTargetsAreRejected()
    {
        await using var dashboard = new Fixture();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/data");
        request.Headers.Host = $"attacker.invalid:{dashboard.Address.Port}";
        using var response = await dashboard.Client.SendAsync(request);
        Assert.False(response.IsSuccessStatusCode);

        using var client = new TcpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(IPAddress.Loopback, dashboard.Address.Port, deadline.Token);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET http://{dashboard.Address.Authority}/api/data HTTP/1.1\r\nHost: {dashboard.Address.Authority}\r\nConnection: close\r\n\r\n"), deadline.Token);
        using var reader = new StreamReader(stream);
        Assert.DoesNotContain(" 200 ", await reader.ReadLineAsync(deadline.Token));
        Assert.Equal(0, dashboard.DataReads);
    }

    [Fact]
    public async Task MissingRoutesAndInternalErrorsDoNotExposeFilesOrExceptionMessages()
    {
        await using var dashboard = new Fixture(failData: true);
        using var unknown = await dashboard.Client.GetAsync("/unknown.js");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var unavailable = await dashboard.Client.GetAsync("/api/data");
        Assert.Equal(HttpStatusCode.InternalServerError, unavailable.StatusCode);
        Assert.DoesNotContain("private-path.db", await unavailable.Content.ReadAsStringAsync());
        Assert.True(unavailable.Headers.Contains("Content-Security-Policy"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        private readonly Task server;
        private int dataReads;

        internal Fixture(bool failData = false)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Address = new Uri($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add(Address.AbsoluteUri);
            listener.Start();
            Client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = Address,
                Timeout = TimeSpan.FromSeconds(10)
            };
            server = ServeAsync(() =>
            {
                Interlocked.Increment(ref dataReads);
                if (failData) throw new IOException("private-path.db");
                return "{}"u8.ToArray();
            });
        }

        internal Uri Address { get; }
        internal HttpClient Client { get; }
        internal int DataReads => Volatile.Read(ref dataReads);

        private async Task ServeAsync(Func<byte[]> data)
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                    DashboardCommand.Respond(await listener.GetContextAsync().WaitAsync(cancellation.Token), Address, data);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync();
            await server;
            Client.Dispose();
            listener.Close();
            cancellation.Dispose();
        }
    }
}
