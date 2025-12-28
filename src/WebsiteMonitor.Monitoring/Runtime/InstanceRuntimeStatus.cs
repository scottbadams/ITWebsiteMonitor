namespace WebsiteMonitor.Monitoring.Runtime;

public enum InstanceRunState
{
    Running = 1,
    Paused = 2
}

public sealed record InstanceRuntimeStatus(
    string InstanceId,
    InstanceRunState State,
    DateTime ChangedUtc,
    string? Message = null);
