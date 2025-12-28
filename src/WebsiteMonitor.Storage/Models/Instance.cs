namespace WebsiteMonitor.Storage.Models;

public sealed class Instance
{
    public string InstanceId { get; set; } = default!; // slug, PK
    public string DisplayName { get; set; } = default!;
    public bool Enabled { get; set; } = true;

    public int CheckIntervalSeconds { get; set; } = 60;
    public int ConcurrencyLimit { get; set; } = 20;

    // IANA timezone, e.g. "America/Phoenix"
    public string TimeZoneId { get; set; } = "America/Phoenix";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
