using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
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

    private static DateTime EnsureUtc(DateTime dt)
        => dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    private static bool IsInstancePaused(Instance instance, DateTime nowUtc, out DateTime? untilUtc)
    {
        untilUtc = null;

        if (instance.IsPaused)
            return true;

        if (instance.PausedUntilUtc.HasValue)
        {
            var u = EnsureUtc(instance.PausedUntilUtc.Value);
            untilUtc = u;
            if (u > nowUtc)
                return true;
        }

        return false;
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
        var intervalSeconds = Math.Max(5, instance.CheckIntervalSeconds);

        if (!instance.Enabled)
        {
            _logger.LogInformation("Instance {InstanceId} is disabled; skipping run.", instanceId);
            return 30;
        }

        if (IsInstancePaused(instance, DateTime.UtcNow, out var pausedUntilUtc))
        {
            if (pausedUntilUtc.HasValue)
                _logger.LogInformation("Instance {InstanceId} is paused until {PausedUntilUtc:o}; skipping run.", instanceId, pausedUntilUtc.Value);
            else
                _logger.LogInformation("Instance {InstanceId} is paused; skipping run.", instanceId);

            // Keep ticking at the configured interval so the instance can automatically resume.
            return intervalSeconds;
        }

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

        // Run checks concurrently (bounded)
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

        // Persist serially and batched
        await PersistBatchAsync(results, ct);

        return intervalSeconds;
    }

    private async Task<TargetCheckResult> CheckOneAsync(Target t, CancellationToken ct)
    {
        var ts = DateTime.UtcNow;

        // Per-target timeout guard (TCP + HTTP combined)
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
            addrs = await Dns.GetHostAddressesAsync(host);
        }
        catch
        {
            addrs = Array.Empty<IPAddress>();
        }

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
            return (false, (int)sw.ElapsedMilliseconds, addrs[0].ToString());
        }

        using (var client = new TcpClient())
        {
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
        if (!Uri.TryCreate(t.Url, UriKind.Absolute, out var startUri))
            return (false, null, null, null, false, null);

        var client = _httpClientFactory.CreateClient("monitor");

        var swTotal = Stopwatch.StartNew();
        try
        {
            // Follow redirects ourselves so we’re not dependent on handler config.
            var (resp, finalUri) = await SendWithRedirectsAsync(client, startUri, maxRedirects: 12, ct);

            using (resp)
            {
                swTotal.Stop();

                // IMPORTANT: if the handler already auto-followed, resp.RequestMessage.RequestUri is the real final.
                var effectiveFinal = resp.RequestMessage?.RequestUri ?? finalUri ?? startUri;
                var finalUrl = effectiveFinal.ToString();

                var code = (int)resp.StatusCode;

                // Default expected range (per-target override already exists)
                var min = t.HttpExpectedStatusMin ?? 200;
                var max = t.HttpExpectedStatusMax ?? 399;
                var ok = code >= min && code <= max;

                // Heuristics detection
                var headerBlob = BuildHeaderBlob(resp);

                bool loginDetected = false;
                string? detectedLoginType = null;

                var mt = resp.Content?.Headers?.ContentType?.MediaType;
                var maybeText =
                    string.IsNullOrWhiteSpace(mt) ||
                    mt.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                    mt.Contains("text", StringComparison.OrdinalIgnoreCase) ||
                    mt.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                    mt.Contains("json", StringComparison.OrdinalIgnoreCase);

                string snippet = "";
                if (maybeText && resp.Content != null)
                {
                    // Rocket.Chat/ERPNext signatures are often in the HTML bootstrap; read a larger snippet.
                    snippet = await ReadBodySnippetAsync(resp.Content, maxBytes: 512 * 1024, ct);
                }

                var det = DetectHeuristics(snippet, finalUrl, headerBlob);

                loginDetected = det.loginDetected;
                detectedLoginType = det.detectedLoginType;

                // PowerShell-style behavior: treat 401/403 as "reachable" (OK) when it's clearly a login surface.
                // This prevents auth challenges from showing as DOWN by default.
                if (!ok && (code == 401 || code == 403) && loginDetected)
                    ok = true;

                return (ok, code, (int)swTotal.ElapsedMilliseconds, finalUrl, loginDetected, detectedLoginType);
            }
        }
        catch
        {
            swTotal.Stop();
            // Transport failure/timeout/etc. => statusCode=null; keep finalUrl as the start URL for visibility.
            return (false, null, (int)swTotal.ElapsedMilliseconds, startUri.ToString(), false, null);
        }
    }

    private static async Task<(HttpResponseMessage resp, Uri? finalUri)> SendWithRedirectsAsync(
        HttpClient client,
        Uri start,
        int maxRedirects,
        CancellationToken ct)
    {
        Uri current = start;
        HttpResponseMessage? resp = null;

        // Small loop detector
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var hop = 0; hop <= maxRedirects; hop++)
        {
            if (!seen.Add(current.ToString()))
            {
                if (resp != null) return (resp, resp.RequestMessage?.RequestUri ?? current);
                break;
            }

            resp?.Dispose();

            using var req = new HttpRequestMessage(HttpMethod.Get, current);

            // Browser-ish headers help some apps return the actual HTML login shell.
            req.Headers.TryAddWithoutValidation("User-Agent", "WebsiteMonitor");
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");

            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!IsRedirect(resp.StatusCode))
                return (resp, resp.RequestMessage?.RequestUri ?? current);

            if (resp.Headers.Location == null)
                return (resp, resp.RequestMessage?.RequestUri ?? current);

            var next = resp.Headers.Location;
            current = next.IsAbsoluteUri ? next : new Uri(current, next);
        }

        if (resp == null) throw new HttpRequestException("No HTTP response.");
        return (resp, resp.RequestMessage?.RequestUri ?? current);
    }

    private static bool IsRedirect(HttpStatusCode code)
        => code == HttpStatusCode.MovedPermanently      // 301
           || code == HttpStatusCode.Found              // 302
           || code == HttpStatusCode.SeeOther           // 303
           || code == HttpStatusCode.TemporaryRedirect  // 307
           || (int)code == 308;                         // Permanent Redirect

    private static string BuildHeaderBlob(HttpResponseMessage resp)
    {
        var sb = new StringBuilder();

        foreach (var h in resp.Headers)
            sb.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).Append('\n');

        if (resp.Content != null)
        {
            foreach (var h in resp.Content.Headers)
                sb.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).Append('\n');
        }

        return sb.ToString();
    }

    private static async Task<string> ReadBodySnippetAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var raw = await content.ReadAsStreamAsync(ct);
        Stream stream = raw;

        // If handler didn’t decompress, do it here based on Content-Encoding.
        try
        {
            if (content.Headers.ContentEncoding != null && content.Headers.ContentEncoding.Count > 0)
            {
                string? enc = null;
                foreach (var e in content.Headers.ContentEncoding)
                    enc = e; // last one wins

                if (!string.IsNullOrWhiteSpace(enc))
                {
                    if (enc.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                        stream = new GZipStream(raw, CompressionMode.Decompress, leaveOpen: false);
                    else if (enc.Equals("deflate", StringComparison.OrdinalIgnoreCase))
                        stream = new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: false);
                    else if (enc.Equals("br", StringComparison.OrdinalIgnoreCase))
                        stream = new BrotliStream(raw, CompressionMode.Decompress, leaveOpen: false);
                }
            }
        }
        catch
        {
            stream = raw;
        }

        var buf = new byte[8192];
        var total = 0;
        using var ms = new MemoryStream(Math.Min(maxBytes, 128 * 1024));

        while (total < maxBytes)
        {
            var toRead = Math.Min(buf.Length, maxBytes - total);
            var read = await stream.ReadAsync(buf.AsMemory(0, toRead), ct);
            if (read <= 0) break;

            ms.Write(buf, 0, read);
            total += read;
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool ContainsIgnoreCase(string haystack, string needle)
        => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string ExtractTitle(string html)
    {
        var start = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";

        var gt = html.IndexOf('>', start);
        if (gt < 0) return "";

        var end = html.IndexOf("</title>", gt, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return "";

        return html.Substring(gt + 1, end - (gt + 1)).Trim();
    }

    // Regexes (compiled once)
    private static readonly Regex RxRocketFinal = new(@"(?i)/home\b|/login\b", RegexOptions.Compiled);
    private static readonly Regex RxRocketStrong = new(@"(?i)\bRocket\.Chat\b|__meteor_runtime_config__|meteor|rc-root|rocketchat", RegexOptions.Compiled);
    private static readonly Regex RxRocketWeak = new(@"(?i)\bRocket\b", RegexOptions.Compiled);

    private static readonly Regex RxErpFinal = new(@"(?i)/login\b|/desk\b", RegexOptions.Compiled);
    private static readonly Regex RxErpHeader = new(@"(?i)\bx-frappe-[a-z0-9\-]+\b", RegexOptions.Compiled);
    private static readonly Regex RxErpStrong = new(@"(?i)\berpnext\b|\bfrappe\b|frappe\.boot|frappe\.csrf_token|/api/method/frappe\.", RegexOptions.Compiled);

    private static (bool loginDetected, string? detectedLoginType) DetectHeuristics(string htmlSnippet, string? finalUrl, string headerBlob)
    {
        var fu = finalUrl ?? "";
        var hb = headerBlob ?? "";
        var h = htmlSnippet ?? "";

        // Even if we couldn't read body, URL/header-only detection can still classify some apps.
        var title = string.IsNullOrWhiteSpace(h) ? "" : ExtractTitle(h);

        // OWA (mail) – includes the errorFE.aspx case you reported
        if (ContainsIgnoreCase(fu, "/owa/")
            || ContainsIgnoreCase(fu, "errorfe.aspx")
            || ContainsIgnoreCase(title, "Outlook")
            || ContainsIgnoreCase(h, "Outlook Web App")
            || ContainsIgnoreCase(h, "owa/auth")
            || ContainsIgnoreCase(h, "owa/auth/logon"))
            return (true, "OWA");

        // Rocket.Chat — mirror your PS rule:
        // StrongFinalRegex: /home|/login
        // StrongContentRegex: Rocket.Chat|meteor
        var rocketFinal = RxRocketFinal.IsMatch(fu);
        var rocketStrong = RxRocketStrong.IsMatch(h) || RxRocketStrong.IsMatch(title);
        var rocketWeak = RxRocketWeak.IsMatch(h) || RxRocketWeak.IsMatch(title);

        if ((rocketStrong && rocketFinal) || rocketStrong || (rocketWeak && rocketFinal))
            return (true, "Rocket.Chat");

        // ERPNext / Frappe
        // Uses body + headers; many deployments include X-Frappe-* headers and/or sid cookie.
        var erpFinal = RxErpFinal.IsMatch(fu);
        var erpHeader =
            RxErpHeader.IsMatch(hb) ||
            ContainsIgnoreCase(hb, "X-Frappe-Site-Name") ||
            ContainsIgnoreCase(hb, "X-Frappe-CMD") ||
            ContainsIgnoreCase(hb, "Set-Cookie: sid=") ||
            ContainsIgnoreCase(hb, " sid=");

        var erpStrong = RxErpStrong.IsMatch(h) || RxErpStrong.IsMatch(title);

        if ((erpStrong && (erpFinal || erpHeader)) || (erpHeader && erpFinal))
            return (true, "ERPNext");

        // Nextcloud
        if (ContainsIgnoreCase(title, "Nextcloud")
            || ContainsIgnoreCase(h, "nextcloud")
            || ContainsIgnoreCase(h, "id=\"body-login\"")
            || ContainsIgnoreCase(h, "body id=\"body-login\"")
            || ContainsIgnoreCase(h, "nc-login"))
            return (true, "Nextcloud");

        // Proxmox VE / PMG / PBS
        if (ContainsIgnoreCase(title, "Proxmox Mail Gateway")
            || ContainsIgnoreCase(h, "Proxmox Mail Gateway")
            || ContainsIgnoreCase(fu, "/pmg"))
            return (true, "Proxmox Mail Gateway");

        if (ContainsIgnoreCase(title, "Proxmox Backup Server")
            || ContainsIgnoreCase(h, "Proxmox Backup Server")
            || ContainsIgnoreCase(fu, "/pbs"))
            return (true, "Proxmox Backup Server");

        if (ContainsIgnoreCase(title, "Proxmox Virtual Environment")
            || ContainsIgnoreCase(h, "Proxmox Virtual Environment")
            || ContainsIgnoreCase(fu, "/pve2/"))
            return (true, "Proxmox VE");

        // Zabbix
        if (ContainsIgnoreCase(title, "Zabbix") || ContainsIgnoreCase(h, "Zabbix"))
            return (true, "Zabbix");

        // OPNsense
        if (ContainsIgnoreCase(title, "OPNsense") || ContainsIgnoreCase(h, "OPNsense"))
            return (true, "OPNsense");

        // CipherMail
        if (ContainsIgnoreCase(title, "CipherMail") || ContainsIgnoreCase(h, "CipherMail"))
            return (true, "CipherMail");

        // Generic login signals
        var hasPassword =
            ContainsIgnoreCase(h, "type=\"password\"") ||
            ContainsIgnoreCase(h, "type='password'");

        var looksLikeLogin =
            ContainsIgnoreCase(h, "login") &&
            (ContainsIgnoreCase(h, "<form")
             || ContainsIgnoreCase(h, "username")
             || ContainsIgnoreCase(h, "email")
             || ContainsIgnoreCase(h, "sign in"));

        if (hasPassword)
            return (true, "PasswordForm");

        if (looksLikeLogin)
            return (true, "LoginPage");

        return (false, null);
    }

    private async Task PersistBatchAsync(List<TargetCheckResult> results, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebsiteMonitorDbContext>();

        try
        {
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

                    state.LastFinalUrl = r.FinalUrl ?? state.LastFinalUrl;
                    state.LastUsedIp = r.UsedIp ?? state.LastUsedIp;

                    // Gate: only update login fields when HTTP actually returned a status code.
                    // (Transport failures return HttpStatusCode=null; do NOT flip the last-known login state/type.)
                    if (r.HttpStatusCode.HasValue)
                    {
                        state.LastDetectedLoginType = r.DetectedLoginType ?? state.LastDetectedLoginType;
                        state.LoginDetectedLast = r.LoginDetected;
                        state.LoginDetectedEver = state.LoginDetectedEver || r.LoginDetected;
                    }

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
                _logger.LogWarning(ex,
                    "SQLite busy/locked; attempt {Attempt}/10 Err={Err} ExtErr={ExtErr}.",
                    attempt, ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);
            }
            finally
            {
                SqliteWriteGate.Gate.Release();
            }

            var delayMs = Math.Min(5000, 100 * attempt * attempt);
            await Task.Delay(delayMs, ct);
        }
    }
}
