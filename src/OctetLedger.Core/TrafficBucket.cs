namespace OctetLedger.Core;

public sealed record TrafficBucket(
    string InterfaceId,
    string InterfaceName,
    DateTimeOffset MinuteUtc,
    long BytesReceived,
    long BytesSent,
    double IntervalSeconds = 60,
    double? PeakBytesPerSecond = null,
    double? LongestIntervalSeconds = null,
    string InterfaceDescription = "",
    string InterfaceType = "");

public sealed record CollectionResult(
    int InterfacesObserved,
    int BaselinesCreated,
    long BytesReceived,
    long BytesSent);

public sealed record TrafficStoreStatus(
    string DatabasePath,
    long DatabaseBytes,
    int TrackedInterfaces,
    long StoredMinutes,
    DateTimeOffset? LastCollectionUtc);
