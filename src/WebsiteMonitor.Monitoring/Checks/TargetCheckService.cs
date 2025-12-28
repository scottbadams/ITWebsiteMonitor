using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.Monitoring.Checks;

public sealed class TargetCheckService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TargetCheckService> _logger;

    public TargetCheckService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<TargetCheckService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<int> RunInstanceOnceAsync(string instanceId, CancellationToken ct)
    {
        // Load instance + targets in ONE scope
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebsiteMonitorDbContext>();

        var instance = await db.Instances.SingleOrDefaultAsync(i => i.InstanceId == instanceId, ct);
        if (instance == null)
        {
            _logger.LogWarning("Instance {InstanceId} not found; skipping run.", instanceId);
            return 30;
        }

        if (!instance.Enabled)
        {
            _logger.LogInformation("Instance {InstanceId} is disabled; skipping run.", instanceId);
            return 30;
        }

        var intervalSeconds = Math.Max(5, instance.CheckIntervalSeconds);
        var limit = Math.Max(1, instance.ConcurrencyLimit);

        var targets = await db.Targets
            .Where(t => t.InstanceId == instanceId && t.Enabled)
            .OrderBy(t => t.TargetId)
            .ToListAsync(ct);

        if (targets.Count == 0)
        {
            _logger.LogInformation("Instance {InstanceId} has no enabled targets.", instanceId);
            return intervalSeconds;
        }

        using var sem = new SemaphoreSlim(limit, limit);

        var tasks = new List<Task>(targets.Count);
        foreach (var t in targets)
        {
            await sem.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await CheckAndPersistOneAsync(t, ct);
                }
                catch (OperationCanceledException)
                {
                    // normal stop
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Target check failed unexpectedly for TargetId={TargetId}", t.TargetId);
                }
                finally
                {
                    sem.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);
        return intervalSeconds;
    }

    private async Task CheckAndPersistOneAsync(Target t, CancellationToken ct)
    {
        var ts = DateTime.UtcNow;

        // Per-target timeout guard (TCP + HTTP combined)
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var tcp = await TcpCheckAsync(t.Url, linked.Token);
        var http = await HttpCheckAsync(t, linked.Token);

        var tcpOk = tcp.ok;
        var httpOk = http.ok;
        var summary = BuildSummary(tcpOk, tcp.latencyMs, httpOk, http.statusCode, http.latencyMs);

        var result = new TargetCheckResult(
            t.TargetId,
            ts,
            tcpOk,
            httpOk,
            http.statusCode,
            tcp.latencyMs,
            http.latencyMs,
            summary);

        await PersistAsync(result, linked.Token);
    }

    private static string BuildSummary(bool tcpOk, int? tcpMs, bool httpOk, int? code, int? httpMs)
    {
        var tcpPart = tcpOk ? $"TCP OK ({tcpMs}ms)" : "TCP FAIL";
        var httpPart = httpOk ? $"HTTP OK ({code}, {httpMs}ms)" : $"HTTP FAIL ({code?.ToString() ?? "no code"})";
        return $"{tcpPart}; {httpPart}";
    }

    private static async Task<(bool ok, int? latencyMs)> TcpCheckAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (false, null);

        var host = uri.Host;
        var port = uri.IsDefaultPort
            ? (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
            : uri.Port;

        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(host, port, ct);
            sw.Stop();
            return (true, (int)sw.ElapsedMilliseconds);
        }
        catch
        {
            sw.Stop();
            return (false, (int)sw.ElapsedMilliseconds);
        }
    }

    private async Task<(bool ok, int? statusCode, int? latencyMs)> HttpCheckAsync(Target t, CancellationToken ct)
    {
        if (!Uri.TryCreate(t.Url, UriKind.Absolute, out var uri))
            return (false, null, null);

        var client = _httpClientFactory.CreateClient("monitor");

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();

            var code = (int)resp.StatusCode;
            var min = t.HttpExpectedStatusMin ?? 200;
            var max = t.HttpExpectedStatusMax ?? 399;

            var ok = code >= min && code <= max;
            return (ok, code, (int)sw.ElapsedMilliseconds);
        }
        catch
        {
            sw.Stop();
            return (false, null, (int)sw.ElapsedMilliseconds);
        }
    }

    private async Task PersistAsync(TargetCheckResult r, CancellationToken ct)
    {
        // IMPORTANT: each persist uses its own scope/dbcontext (thread-safe)
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebsiteMonitorDbContext>();

        // Insert check history row
        db.Checks.Add(new Check
        {
            TargetId = r.TargetId,
            TimestampUtc = r.TimestampUtc,
            TcpOk = r.TcpOk,
            HttpOk = r.HttpOk,
            HttpStatusCode = r.HttpStatusCode,
            TcpLatencyMs = r.TcpLatencyMs,
            HttpLatencyMs = r.HttpLatencyMs,
            Summary = r.Summary
        });

        // Upsert state row
        var state = await db.States.SingleOrDefaultAsync(s => s.TargetId == r.TargetId, ct);
        var isUp = r.TcpOk && r.HttpOk;

        if (state == null)
        {
            state = new TargetState
            {
                TargetId = r.TargetId,
                IsUp = isUp,
                LastCheckUtc = r.TimestampUtc,
                StateSinceUtc = r.TimestampUtc,
                LastChangeUtc = r.TimestampUtc,
                ConsecutiveFailures = isUp ? 0 : 1,
                LastSummary = r.Summary
            };
            db.States.Add(state);
        }
        else
        {
            var changed = state.IsUp != isUp;

            state.LastCheckUtc = r.TimestampUtc;
            state.LastSummary = r.Summary;

            if (changed)
            {
                state.IsUp = isUp;
                state.LastChangeUtc = r.TimestampUtc;
                state.StateSinceUtc = r.TimestampUtc;
                state.ConsecutiveFailures = isUp ? 0 : 1;
            }
            else
            {
                state.ConsecutiveFailures = isUp ? 0 : (state.ConsecutiveFailures + 1);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
