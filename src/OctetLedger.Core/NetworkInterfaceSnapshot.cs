namespace OctetLedger.Core;

public sealed record NetworkInterfaceSnapshot(
    string Id,
    string Name,
    string Description,
    string Type,
    string Status,
    long SpeedBitsPerSecond,
    long BytesReceived,
    long BytesSent,
    DateTimeOffset CapturedAt);
