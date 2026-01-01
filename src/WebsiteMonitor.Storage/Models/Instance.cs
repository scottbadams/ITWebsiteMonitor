namespace WebsiteMonitor.Storage.Models;

public sealed class Instance
{
    // slug, PK
    public string InstanceId { get; set; } = default!;

    public string DisplayName { get; set; } = default!;

    // Instance enabled/disabled (disabled = do not run checks)
    public bool Enabled { get; set; } = true;

    // Pause is separate from Enabled.
    // Enabled=true but IsPaused=true means: instance is active/configured, but checks are temporarily stopped.
    public bool IsPaused { get; set; } = false;

    // Optional timed pause. If set to a future UTC time, instance is considered paused until then.
    public DateTime? PausedUntilUtc { get; set; } = null;

    public int CheckIntervalSeconds { get; set; } = 60;
    public int ConcurrencyLimit { get; set; } = 20;

    // Store IANA timezone string (you requested IANA)
    public string TimeZoneId { get; set; } = "America/Phoenix";

    // Output settings (used later when we write static HTML snapshots)
    public bool WriteHtmlSnapshot { get; set; } = true;
    public string? OutputFolder { get; set; } = null;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastRunUtc { get; set; }
}
