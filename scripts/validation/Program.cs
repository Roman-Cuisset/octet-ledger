using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OctetLedger.Core;

if (args.Length == 3 && args[0] == "totals")
{
    var row = ApplicationTrafficStore.ReadTop(DateTimeOffset.UnixEpoch, int.MaxValue, args[1])
        .SingleOrDefault(value => string.Equals(value.ProcessName, args[2], StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        processName = args[2],
        bytesReceived = row?.BytesReceived ?? 0,
        bytesSent = row?.BytesSent ?? 0,
        totalBytes = row?.TotalBytes ?? 0
    }));
    return row is null ? 1 : 0;
}

if (args.Length != 1 || args[0] is not ("tcp" or "udp"))
{
    Console.Error.WriteLine("Usage: OctetLedger.IPv6Probe tcp|udp|totals <fixture-database> <process-name>");
    return 2;
}

if (!Socket.OSSupportsIPv6)
{
    Console.Error.WriteLine("UNSUPPORTED: Windows reports no IPv6 socket support.");
    return 3;
}

try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var token = timeout.Token;
    var payload = new byte[16 * 1024];
    Random.Shared.NextBytes(payload);
    long transferred = 0;
    var started = DateTimeOffset.UtcNow;
    var timer = Stopwatch.StartNew();
    if (args[0] == "tcp")
    {
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Server.DualMode = false;
        listener.Start();
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetworkV6);
            var accepting = listener.AcceptTcpClientAsync(token);
            await client.ConnectAsync(IPAddress.IPv6Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
            using var server = await accepting;
            var received = new byte[payload.Length];
            while (timer.Elapsed < TimeSpan.FromSeconds(6))
            {
                var reading = server.GetStream().ReadExactlyAsync(received, token);
                await client.GetStream().WriteAsync(payload, token);
                await reading;
                if (!received.AsSpan().SequenceEqual(payload)) throw new IOException("TCP payload mismatch.");
                transferred += payload.Length;
                await Task.Delay(10, token);
            }
        }
        finally { listener.Stop(); }
    }
    else
    {
        using var server = new UdpClient(AddressFamily.InterNetworkV6);
        server.Client.DualMode = false;
        server.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        using var client = new UdpClient(AddressFamily.InterNetworkV6);
        client.Connect(IPAddress.IPv6Loopback, ((IPEndPoint)server.Client.LocalEndPoint!).Port);
        while (timer.Elapsed < TimeSpan.FromSeconds(6))
        {
            var receiving = server.ReceiveAsync(token);
            await client.SendAsync(payload, token);
            var result = await receiving;
            if (!result.Buffer.AsSpan().SequenceEqual(payload)) throw new IOException("UDP payload mismatch.");
            if (!IPAddress.IsLoopback(result.RemoteEndPoint.Address)) throw new IOException("Non-loopback UDP peer.");
            transferred += payload.Length;
            await Task.Delay(10, token);
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        transport = args[0], address = "::1", addressFamily = "InterNetworkV6",
        startedUtc = started, completedUtc = DateTimeOffset.UtcNow,
        processId = Environment.ProcessId, payloadBytesSent = transferred, payloadBytesReceived = transferred
    }));
    // Keep process identity available while the live ETW consumer drains buffered events.
    await Task.Delay(2000, token);
    return 0;
}
catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
{
    Console.Error.WriteLine($"BLOCKED: IPv6 loopback fixture failed: {exception.Message}");
    return 4;
}
