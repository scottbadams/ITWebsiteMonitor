namespace WebsiteMonitor.Storage.Models;

public sealed class Instance
{
    // slug, PK
    public string InstanceId { get; set; } = default!;

    public string DisplayName { get; set; } = default!;
    public bool Enabled { get; set; } = true;

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
