namespace WebsiteMonitor.Monitoring.Alerting;

public sealed class AlertingOptions
{
    public int SchedulerTickSeconds { get; set; } = 15;

    public int DownAfterSeconds { get; set; } = 180;       // 3 minutes
    public int RecoveredAfterSeconds { get; set; } = 60;   // 1 minute

    public int RepeatEverySeconds_Under24h { get; set; } = 1800; // 30 minutes
    public int RepeatEverySeconds_24hTo72h { get; set; } = 3600; // 1 hour

    public int DailyAfterHours { get; set; } = 72; // switch to daily schedule after 72h down

    public int DailyHourLocal { get; set; } = 10;
    public int DailyMinuteLocal { get; set; } = 0;

    // Absolute base URL used in alert emails for logo and "Open Dashboard" link.
    // Example: https://monitor.example.com  (no trailing slash required)
    public string? PublicBaseUrl { get; set; } = null;
}
