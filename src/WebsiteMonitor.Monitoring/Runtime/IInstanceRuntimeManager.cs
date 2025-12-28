namespace WebsiteMonitor.Monitoring.Runtime;

public interface IInstanceRuntimeManager
{
    IReadOnlyCollection<InstanceRuntimeStatus> GetAll();
    bool TryGet(string instanceId, out InstanceRuntimeStatus status);

    Task StartAsync(string instanceId, CancellationToken ct = default);
    Task StopAsync(string instanceId, CancellationToken ct = default);
    Task RestartAsync(string instanceId, CancellationToken ct = default);
}
