namespace WebsiteMonitor.Storage.Models;

public sealed class TargetState
{
    public long TargetId { get; set; } // PK == FK to Targets

    public bool IsUp { get; set; }
    public DateTime LastCheckUtc { get; set; }
    public DateTime StateSinceUtc { get; set; }
    public DateTime LastChangeUtc { get; set; }

	public string? LastFinalUrl { get; set; }
	public string? LastUsedIp { get; set; }
	public string? LastDetectedLoginType { get; set; }

	public bool LoginDetectedEver { get; set; }
	public bool LoginDetectedLast { get; set; }

    public int ConsecutiveFailures { get; set; }
    public string LastSummary { get; set; } = "";

    // Alert tracking (UTC)
    public DateTime? DownFirstNotifiedUtc { get; set; }
    public DateTime? LastNotifiedUtc { get; set; }
    public DateTime? NextNotifyUtc { get; set; }

    public DateTime? RecoveredDueUtc { get; set; }
    public DateTime? RecoveredNotifiedUtc { get; set; }
}
