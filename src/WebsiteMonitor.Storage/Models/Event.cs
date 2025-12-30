namespace WebsiteMonitor.Storage.Models;

public sealed class Event
{
    public long EventId { get; set; }

    public string InstanceId { get; set; } = "";
    public long? TargetId { get; set; }

    public DateTime TimestampUtc { get; set; }

    // Examples: TransitionDown, TransitionUp, AlertDown, AlertDownRepeat, AlertRecovered, Error
    public string Type { get; set; } = "";

    public string Message { get; set; } = "";
}
