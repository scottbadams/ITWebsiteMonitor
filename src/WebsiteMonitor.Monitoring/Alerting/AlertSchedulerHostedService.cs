using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebsiteMonitor.Monitoring.Runtime;

namespace WebsiteMonitor.Monitoring.Alerting;

public sealed class AlertSchedulerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInstanceRuntimeManager _runtime;
    private readonly AlertingOptions _opt;
    private readonly ILogger<AlertSchedulerHostedService> _logger;

    public AlertSchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        IInstanceRuntimeManager runtime,
        IOptions<AlertingOptions> opt,
        ILogger<AlertSchedulerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _runtime = runtime;
        _opt = opt.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromSeconds(Math.Max(5, _opt.SchedulerTickSeconds));
        using var timer = new PeriodicTimer(tick);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var statuses = _runtime.GetAll();

                foreach (var s in statuses)
                {
                    var running = s.State == InstanceRunState.Running;

                    using var scope = _scopeFactory.CreateScope();
                    var evaluator = scope.ServiceProvider.GetRequiredService<AlertEvaluator>();
                    await evaluator.EvaluateInstanceAsync(s.InstanceId, running, nowUtc, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alert scheduler tick failed.");
            }
        }
    }
}
