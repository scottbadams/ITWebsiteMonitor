namespace WebsiteMonitor.Storage.Models;

public sealed class Target
{
    public long TargetId { get; set; } // PK
    public string InstanceId { get; set; } = default!; // FK to Instances

    public string Url { get; set; } = default!;
    public bool Enabled { get; set; } = true;

    // later: login rule name, headers, thresholds
    public string? LoginRule { get; set; } = null;

    public int? HttpExpectedStatusMin { get; set; } = 200;
    public int? HttpExpectedStatusMax { get; set; } = 399;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
