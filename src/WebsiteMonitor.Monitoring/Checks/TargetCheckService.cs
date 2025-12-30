using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
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
        // Read instance + targets in ONE scope
        using var readScope = _scopeFactory.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<WebsiteMonitorDbContext>();

        var instance = await readDb.Instances.SingleOrDefaultAsync(i => i.InstanceId == instanceId, ct);
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

        var targets = await readDb.Targets
            .Where(t => t.InstanceId == instanceId && t.Enabled)
            .OrderBy(t => t.TargetId)
            .ToListAsync(ct);

        if (targets.Count == 0)
        {
            _logger.LogInformation("Instance {InstanceId} has no enabled targets.", instanceId);
            return intervalSeconds;
        }

        // 1) Run checks concurrently (bounded)
        using var sem = new SemaphoreSlim(limit, limit);

        var tasks = new List<Task<TargetCheckResult?>>(targets.Count);
        foreach (var t in targets)
        {
            var target = t;
            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    return await CheckOneAsync(target, ct);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Target check failed unexpectedly for TargetId={TargetId}", target.TargetId);
                    return null;
                }
                finally
                {
                    sem.Release();
                }
            }, ct));
        }

        var results = (await Task.WhenAll(tasks))
            .Where(r => r != null)
            .Cast<TargetCheckResult>()
            .ToList();

        if (results.Count == 0)
            return intervalSeconds;

        // 2) Persist serially and batched (one SaveChanges per tick)
        await PersistBatchAsync(results, ct);

        return intervalSeconds;
    }

    private async Task<TargetCheckResult> CheckOneAsync(Target t, CancellationToken ct)
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

        return new TargetCheckResult(
            t.InstanceId,
            t.TargetId,
            t.Url,
            ts,
            tcpOk,
            httpOk,
            http.statusCode,
            tcp.latencyMs,
            http.latencyMs,
            summary);
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

    private async Task PersistBatchAsync(List<TargetCheckResult> results, CancellationToken ct)
    {
        // Single scope + single DbContext for the entire batch (serial writes)
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebsiteMonitorDbContext>();

        try
        {
            // Preload all existing state rows in one query
            var targetIds = results.Select(r => r.TargetId).Distinct().ToList();
            var stateMap = await db.States
                .Where(s => targetIds.Contains(s.TargetId))
                .ToDictionaryAsync(s => s.TargetId, ct);

            foreach (var r in results)
            {
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

                var isUp = r.TcpOk && r.HttpOk;

                if (!stateMap.TryGetValue(r.TargetId, out var state))
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
                    stateMap[r.TargetId] = state;
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
            }

            await SaveChangesWithSqliteRetryAsync(db, ct);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex,
                "SQLite failure PersistBatchAsync(Count={Count}) Err={Err} ExtErr={ExtErr} Message={Message}",
                results.Count, ex.SqliteErrorCode, ex.SqliteExtendedErrorCode, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PersistBatchAsync failed.");
        }
    }

    private async Task SaveChangesWithSqliteRetryAsync(WebsiteMonitorDbContext db, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await SqliteWriteGate.Gate.WaitAsync(ct);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (SqliteException ex) when ((ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6) && attempt <= 10)
            {
                // 5=SQLITE_BUSY, 6=SQLITE_LOCKED
                _logger.LogWarning(ex,
                    "SQLite busy/locked; attempt {Attempt}/10 Err={Err} ExtErr={ExtErr}.",
                    attempt, ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);
            }
            finally
            {
                SqliteWriteGate.Gate.Release();
            }

            // Backoff outside the gate
            var delayMs = Math.Min(5000, 100 * attempt * attempt); // 100, 400, 900, ...
            await Task.Delay(delayMs, ct);
        }
    }
}
