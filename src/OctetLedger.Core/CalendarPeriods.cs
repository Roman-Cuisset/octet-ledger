namespace OctetLedger.Core;

internal static class CalendarPeriods
{
    internal static DateTimeOffset StartOfDayUtc(DateTime localDate, TimeZoneInfo timeZone)
    {
        var boundary = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(boundary)) boundary = boundary.AddMinutes(1);
        var offset = timeZone.IsAmbiguousTime(boundary)
            ? timeZone.GetAmbiguousTimeOffsets(boundary).Max()
            : timeZone.GetUtcOffset(boundary);
        return new DateTimeOffset(boundary, offset).ToUniversalTime();
    }
}
