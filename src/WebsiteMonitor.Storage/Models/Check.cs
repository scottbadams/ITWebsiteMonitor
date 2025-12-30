namespace WebsiteMonitor.Storage.Models;

public sealed class Check
{
    public long CheckId { get; set; } // PK
    public long TargetId { get; set; } // FK

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public bool TcpOk { get; set; }
    public bool HttpOk { get; set; }

    public int? HttpStatusCode { get; set; }
    public int? TcpLatencyMs { get; set; }
    public int? HttpLatencyMs { get; set; }

	public string? FinalUrl { get; set; }
	public string? UsedIp { get; set; }
	public string? DetectedLoginType { get; set; }
	public bool LoginDetected { get; set; }

    public string Summary { get; set; } = "";
}
