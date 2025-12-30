using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
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
            InstanceId: t.InstanceId,
            TargetId: t.TargetId,
            Url: t.Url,
            TimestampUtc: ts,
            TcpOk: tcpOk,
            HttpOk: httpOk,
            HttpStatusCode: http.statusCode,
            TcpLatencyMs: tcp.latencyMs,
            HttpLatencyMs: http.latencyMs,
            Summary: summary,
            FinalUrl: http.finalUrl,
            UsedIp: tcp.usedIp,
            LoginDetected: http.loginDetected,
            DetectedLoginType: http.detectedLoginType);
    }

    private static string BuildSummary(bool tcpOk, int? tcpMs, bool httpOk, int? code, int? httpMs)
    {
        var tcpPart = tcpOk ? $"TCP OK ({tcpMs}ms)" : "TCP FAIL";
        var httpPart = httpOk ? $"HTTP OK ({code}, {httpMs}ms)" : $"HTTP FAIL ({code?.ToString() ?? "no code"})";
        return $"{tcpPart}; {httpPart}";
    }

    private static async Task<(bool ok, int? latencyMs, string? usedIp)> TcpCheckAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (false, null, null);

        var host = uri.Host;
        var port = uri.IsDefaultPort
            ? (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
            : uri.Port;

        IPAddress[] addrs;
        try
        {
            // Note: no CT overload on all platforms; keep it simple.
            addrs = await Dns.GetHostAddressesAsync(host);
        }
        catch
        {
            addrs = Array.Empty<IPAddress>();
        }

        // We'll try resolved IPs first (so we can record UsedIp).
        // If no resolved IPs, fall back to hostname connect (UsedIp unknown).
        var sw = Stopwatch.StartNew();

        if (addrs.Length > 0)
        {
            foreach (var ip in addrs)
            {
                using var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(new IPEndPoint(ip, port), ct);
                    sw.Stop();
                    return (true, (int)sw.ElapsedMilliseconds, ip.ToString());
                }
                catch
                {
                    // try next
                }
            }

            sw.Stop();
            // If all IPs failed, still report the first IP we attempted (best-effort visibility).
            return (false, (int)sw.ElapsedMilliseconds, addrs[0].ToString());
        }
        else
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(host, port, ct);
                sw.Stop();
                return (true, (int)sw.ElapsedMilliseconds, null);
            }
            catch
            {
                sw.Stop();
                return (false, (int)sw.ElapsedMilliseconds, null);
            }
        }
    }

    private async Task<(bool ok, int? statusCode, int? latencyMs, string? finalUrl, bool loginDetected, string? detectedLoginType)> HttpCheckAsync(Target t, CancellationToken ct)
    {
        if (!Uri.TryCreate(t.Url, UriKind.Absolute, out var uri))
            return (false, null, null, null, false, null);

        var client = _httpClientFactory.CreateClient("monitor");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();

            // Final URL after redirects (if redirects are enabled in the handler)
            string? finalUrl = resp.RequestMessage?.RequestUri?.ToString();

            var code = (int)resp.StatusCode;
            var min = t.HttpExpectedStatusMin ?? 200;
            var max = t.HttpExpectedStatusMax ?? 399;

            var ok = code >= min && code <= max;

            // Minimal login detection heuristic:
            // - only attempt if response is HTML-ish
            bool loginDetected = false;
            string? detectedLoginType = null;

            if (resp.Content?.Headers?.ContentType?.MediaType != null)
            {
                var mt = resp.Content.Headers.ContentType.MediaType;
                if (mt.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    var snippet = await ReadBodySnippetAsync(resp.Content, maxBytes: 64 * 1024, ct);

                    // Very simple signals
                    if (ContainsIgnoreCase(snippet, "type=\"password\"") || ContainsIgnoreCase(snippet, "type='password'"))
                    {
                        loginDetected = true;
                        detectedLoginType = "PasswordForm";
                    }
                    else if (ContainsIgnoreCase(snippet, "login") && (ContainsIgnoreCase(snippet, "username") || ContainsIgnoreCase(snippet, "email")))
                    {
                        loginDetected = true;
                        detectedLoginType = "LoginPage";
                    }
                }
            }

            return (ok, code, (int)sw.ElapsedMilliseconds, finalUrl, loginDetected, detectedLoginType);
        }
        catch
        {
            sw.Stop();
            return (false, null, (int)sw.ElapsedMilliseconds, null, false, null);
        }
    }

    private static async Task<string> ReadBodySnippetAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);

        var buf = new byte[8192];
        var total = 0;
        using var ms = new MemoryStream(Math.Min(maxBytes, 64 * 1024));

        while (total < maxBytes)
        {
            var toRead = Math.Min(buf.Length, maxBytes - total);
            var read = await stream.ReadAsync(buf.AsMemory(0, toRead), ct);
            if (read <= 0) break;

            ms.Write(buf, 0, read);
            total += read;
        }

        // Best-effort decode. (If it isn't UTF-8, the heuristic will just be less accurate.)
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool ContainsIgnoreCase(string haystack, string needle)
        => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

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
                    Summary = r.Summary,

                    FinalUrl = r.FinalUrl,
                    UsedIp = r.UsedIp,
                    DetectedLoginType = r.DetectedLoginType,
                    LoginDetected = r.LoginDetected
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
                        LastSummary = r.Summary,

                        LastFinalUrl = r.FinalUrl,
                        LastUsedIp = r.UsedIp,
                        LastDetectedLoginType = r.DetectedLoginType,
                        LoginDetectedLast = r.LoginDetected,
                        LoginDetectedEver = r.LoginDetected
                    };

                    db.States.Add(state);
                    stateMap[r.TargetId] = state;
                }
                else
                {
                    var changed = state.IsUp != isUp;

                    state.LastCheckUtc = r.TimestampUtc;
                    state.LastSummary = r.Summary;

                    // Carry forward the most recent observed values (even if DOWN)
                    state.LastFinalUrl = r.FinalUrl ?? state.LastFinalUrl;
                    state.LastUsedIp = r.UsedIp ?? state.LastUsedIp;
                    state.LastDetectedLoginType = r.DetectedLoginType ?? state.LastDetectedLoginType;
                    state.LoginDetectedLast = r.LoginDetected;
                    state.LoginDetectedEver = state.LoginDetectedEver || r.LoginDetected;

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
