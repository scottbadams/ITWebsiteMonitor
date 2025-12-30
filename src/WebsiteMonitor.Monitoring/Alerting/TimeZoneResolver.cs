using TimeZoneConverter;

namespace WebsiteMonitor.Monitoring.Alerting;

public sealed class TimeZoneResolver
{
    public TimeZoneInfo ResolveFromIana(string ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId))
            return TimeZoneInfo.Utc;

        // Try direct first (works on Linux/macOS; on many Windows builds too).
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch
        {
            // Fallback: map IANA -> Windows ID, then load.
            var windowsId = TZConvert.IanaToWindows(ianaId);
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
    }

    public DateTime ToLocal(DateTime utc, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);

    public DateTime ToUtc(DateTime localUnspecified, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified), tz);
}
