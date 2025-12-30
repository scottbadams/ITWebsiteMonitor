namespace WebsiteMonitor.App.Snapshots;

public sealed class SnapshotOptions
{
    // How often to regenerate snapshots (seconds)
    public int TickSeconds { get; set; } = 15;

    // How many recent checks to include per target
    public int HistoryCount { get; set; } = 10;
}
