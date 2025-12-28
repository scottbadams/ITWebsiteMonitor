using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebsiteMonitor.Monitoring.Runtime;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.Monitoring.HostedServices;

public sealed class InstanceAutoStartHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInstanceRuntimeManager _runtime;
    private readonly ILogger<InstanceAutoStartHostedService> _logger;

    public InstanceAutoStartHostedService(
        IServiceScopeFactory scopeFactory,
        IInstanceRuntimeManager runtime,
        ILogger<InstanceAutoStartHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start all Enabled instances at app start
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WebsiteMonitorDbContext>();

            var enabledInstanceIds = await db.Instances
                .Where(i => i.Enabled)
                .Select(i => i.InstanceId)
                .ToListAsync(stoppingToken);

            foreach (var id in enabledInstanceIds)
            {
                _logger.LogInformation("Auto-starting enabled instance {InstanceId}.", id);
                await _runtime.StartAsync(id, stoppingToken);
            }
        }

        // Keep service alive
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
