namespace OctetLedger.Core;

public sealed record TrafficRate(double ReceivedBytesPerSecond, double SentBytesPerSecond)
{
    public double TotalBytesPerSecond => ReceivedBytesPerSecond + SentBytesPerSecond;
}

public static class TrafficRateCalculator
{
    public static TrafficRate Calculate(NetworkInterfaceSnapshot previous, NetworkInterfaceSnapshot current)
    {
        if (!string.Equals(previous.Id, current.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Snapshots must belong to the same network interface.");
        }

        var elapsedSeconds = (current.CapturedAt - previous.CapturedAt).TotalSeconds;
        if (elapsedSeconds <= 0 ||
            current.BytesReceived < previous.BytesReceived ||
            current.BytesSent < previous.BytesSent)
        {
            return new TrafficRate(0, 0);
        }

        var receivedDelta = current.BytesReceived - previous.BytesReceived;
        var sentDelta = current.BytesSent - previous.BytesSent;

        return new TrafficRate(receivedDelta / elapsedSeconds, sentDelta / elapsedSeconds);
    }
}
