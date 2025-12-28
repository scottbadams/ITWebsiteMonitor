using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WebsiteMonitor.Monitoring.Runtime;

public sealed class InstanceRuntimeManager : IInstanceRuntimeManager
{
    private sealed class Worker
    {
        public string InstanceId { get; }
        public CancellationTokenSource? Cts { get; set; }
        public Task? Task { get; set; }
        public InstanceRuntimeStatus Status { get; set; }

        public Worker(string instanceId)
        {
            InstanceId = instanceId;
            Status = new InstanceRuntimeStatus(instanceId, InstanceRunState.Paused, DateTime.UtcNow, "Initialized");
        }
    }

    private readonly ConcurrentDictionary<string, Worker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InstanceRuntimeManager> _logger;

    public InstanceRuntimeManager(ILogger<InstanceRuntimeManager> logger)
    {
        _logger = logger;
    }

    public IReadOnlyCollection<InstanceRuntimeStatus> GetAll()
        => _workers.Values.Select(w => w.Status).OrderBy(s => s.InstanceId).ToList();

    public bool TryGet(string instanceId, out InstanceRuntimeStatus status)
    {
        if (_workers.TryGetValue(instanceId, out var w))
        {
            status = w.Status;
            return true;
        }

        status = new InstanceRuntimeStatus(instanceId, InstanceRunState.Paused, DateTime.UtcNow, "Not created");
        return false;
    }

    public async Task StartAsync(string instanceId, CancellationToken ct = default)
    {
        var w = _workers.GetOrAdd(instanceId, id => new Worker(id));

        // Already running -> no-op
        if (w.Status.State == InstanceRunState.Running && w.Task is { IsCompleted: false })
            return;

        // Cancel any old worker
        if (w.Cts != null)
        {
            try { w.Cts.Cancel(); } catch { }
        }

        w.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        w.Status = new InstanceRuntimeStatus(instanceId, InstanceRunState.Running, DateTime.UtcNow, "Started");

        w.Task = Task.Run(async () =>
        {
            _logger.LogInformation("Instance {InstanceId} runtime loop started.", instanceId);

            try
            {
                // Placeholder loop (real monitoring comes later)
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                while (await timer.WaitForNextTickAsync(w.Cts.Token))
                {
                    _logger.LogInformation("Instance {InstanceId} tick.", instanceId);
                }
            }
            catch (OperationCanceledException)
            {
                // normal stop
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Instance {InstanceId} runtime loop crashed.", instanceId);
                w.Status = new InstanceRuntimeStatus(instanceId, InstanceRunState.Paused, DateTime.UtcNow, "Crashed");
            }
            finally
            {
                _logger.LogInformation("Instance {InstanceId} runtime loop stopped.", instanceId);
            }
        }, w.Cts.Token);

        await Task.CompletedTask;
    }

    public async Task StopAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_workers.TryGetValue(instanceId, out var w))
        {
            _workers.TryAdd(instanceId, new Worker(instanceId));
            return;
        }

        w.Status = new InstanceRuntimeStatus(instanceId, InstanceRunState.Paused, DateTime.UtcNow, "Stopped (runtime pause)");

        if (w.Cts != null)
        {
            try { w.Cts.Cancel(); } catch { }
        }

        if (w.Task != null)
        {
            try { await w.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch { }
        }
    }

    public async Task RestartAsync(string instanceId, CancellationToken ct = default)
    {
        await StopAsync(instanceId, ct);
        await StartAsync(instanceId, ct);
    }
}
