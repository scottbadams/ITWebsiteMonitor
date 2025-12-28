namespace WebsiteMonitor.Storage.Models;

public sealed class TargetState
{
    public long TargetId { get; set; } // PK == FK to Targets

    public bool IsUp { get; set; }
    public DateTime LastCheckUtc { get; set; }
    public DateTime StateSinceUtc { get; set; }
    public DateTime LastChangeUtc { get; set; }

    public int ConsecutiveFailures { get; set; }
    public string LastSummary { get; set; } = "";
}
