using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebsiteMonitor.App.Infrastructure;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.App.Snapshots;

public sealed class HtmlSnapshotHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProductPaths _paths;
    private readonly SnapshotOptions _opt;
    private readonly ILogger<HtmlSnapshotHostedService> _logger;

    public HtmlSnapshotHostedService(
        IServiceScopeFactory scopeFactory,
        ProductPaths paths,
        IOptions<SnapshotOptions> opt,
        ILogger<HtmlSnapshotHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _paths = paths;
        _opt = opt.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromSeconds(Math.Max(5, _opt.TickSeconds));
        using var timer = new PeriodicTimer(tick);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await WriteAllSnapshotsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot tick failed.");
            }
        }
    }

    private async Task WriteAllSnapshotsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebsiteMonitorDbContext>();

        string baseUrl;
        try
        {
            var configured = await db.SystemSettings.AsNoTracking()
                .Where(s => s.Id == 1)
                .Select(s => s.PublicBaseUrl)
                .FirstOrDefaultAsync(ct);
            baseUrl = NormalizeBaseUrl(configured);
        }
        catch
        {
            baseUrl = "http://localhost:5041";
        }


        var instances = await db.Instances
            .AsNoTracking()
            .Where(i => i.Enabled && i.WriteHtmlSnapshot && i.OutputFolder != null && i.OutputFolder != "")
            .ToListAsync(ct);

        foreach (var inst in instances)
        {
            try
            {
                await WriteOneInstanceSnapshotAsync(db, inst.InstanceId, inst.DisplayName, inst.TimeZoneId, inst.OutputFolder!, baseUrl, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot write failed. Instance={InstanceId}", inst.InstanceId);
            }
        }
    }

    private async Task WriteOneInstanceSnapshotAsync(
        WebsiteMonitorDbContext db,
        string instanceId,
        string displayName,
        string? timeZoneId,
        string outputFolderRaw,
        string baseUrl,
        CancellationToken ct)
    {
        // Relative -> under DataRoot. Absolute -> use as-is.
        var outputFolder = Path.IsPathRooted(outputFolderRaw)
            ? outputFolderRaw
            : Path.Combine(_paths.DataRoot, outputFolderRaw);

        Directory.CreateDirectory(outputFolder);

        // Per-target snapshots live under {outputFolder}\targets\{TargetId}.html
        var targetsFolder = Path.Combine(outputFolder, "targets");
        Directory.CreateDirectory(targetsFolder);

        var tz = ResolveTimeZone(timeZoneId, _logger);
        var tzLabel = string.IsNullOrWhiteSpace(timeZoneId) ? TimeZoneInfo.Local.Id : timeZoneId!.Trim();

        var targets = await db.Targets
            .AsNoTracking()
            .Where(t => t.InstanceId == instanceId && t.Enabled)
            .OrderBy(t => t.Url)
            .ToListAsync(ct);

        if (targets.Count == 0)
            return;

        var targetIds = targets.Select(t => t.TargetId).ToList();

        var states = await db.States
            .AsNoTracking()
            .Where(s => targetIds.Contains(s.TargetId))
            .ToDictionaryAsync(s => s.TargetId, ct);

        // Pull a bounded set of recent checks across all targets, then group in-memory.
        var perTargetN = Math.Max(1, _opt.HistoryCount);
        var maxChecks = perTargetN * targets.Count;

        var recentChecks = await db.Checks
            .AsNoTracking()
            .Where(c => targetIds.Contains(c.TargetId))
            .OrderByDescending(c => c.TimestampUtc)
            .Take(maxChecks)
            .ToListAsync(ct);

        var recentByTarget = recentChecks
            .GroupBy(c => c.TargetId)
            .ToDictionary(g => g.Key, g => g.Take(perTargetN).ToList());

        // “Last Run” (snapshot context): latest check timestamp we included
        DateTime? lastRunUtc = recentChecks.Count == 0 ? null : recentChecks.Max(c => c.TimestampUtc);

        var nowUtc = DateTime.UtcNow;

        // -------------------------
        // Write per-instance snapshot
        // -------------------------
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
sb.AppendLine("<html lang=\"en\"><head>");
sb.AppendLine("<meta charset=\"utf-8\"/>");
sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"/>");
sb.AppendLine("<meta name=\"color-scheme\" content=\"light dark\"/>");
sb.AppendLine($"<title>ITWebsiteMonitor - Snapshot - {Html(displayName)}</title>");
sb.AppendLine($"<link rel=\"stylesheet\" href=\"{HtmlAttr(baseUrl)}/css/site.css\" />");
sb.AppendLine("<style>.wm-iframe-only{display:none!important} body.wm-in-iframe .wm-iframe-hide{display:none!important} body.wm-in-iframe .wm-iframe-only{display:inline-flex!important}</style>");
sb.AppendLine("<script>(function(){var framed=false;try{framed=window.self!==window.top;}catch(e){framed=true;}if(!framed)return;document.addEventListener(\"DOMContentLoaded\",function(){document.body.classList.add(\"wm-in-iframe\");var t=document.getElementById(\"wm-snap-title\");if(t){var dn=t.getAttribute(\"data-displayname\")||\"\";t.textContent=\"Snapshot - \"+dn;}});})();</script>");
sb.AppendLine("</head><body>");

sb.AppendLine("<div class=\"wm-page\">");
sb.AppendLine("<div class=\"wm-topbar\">");
sb.AppendLine("<div class=\"wm-topbar-title\">");
sb.AppendLine($"<img class=\"wm-logo wm-iframe-hide\" src=\"{HtmlAttr(baseUrl)}/images/itgreatfalls-logo.png\" alt=\"Logo\" />");
sb.AppendLine($"<span id=\"wm-snap-title\" data-displayname=\"{HtmlAttr(displayName)}\">ITWebsiteMonitor - Snapshot - {Html(displayName)}</span>");
sb.AppendLine("</div>");
sb.AppendLine("<div class=\"wm-topbar-actions\">");
sb.AppendLine($"<a class=\"wm-btn wm-iframe-hide\" href=\"{HtmlAttr(baseUrl)}/\">Home</a>");
sb.AppendLine($"<a class=\"wm-btn wm-iframe-hide\" href=\"{HtmlAttr(baseUrl)}/monitor/{HtmlAttr(instanceId)}\">Monitor</a>");
sb.AppendLine($"<a class=\"wm-btn wm-iframe-hide\" href=\"{HtmlAttr(baseUrl)}/setup\">Setup</a>");
sb.AppendLine($"<a class=\"wm-btn wm-iframe-hide\" href=\"{HtmlAttr(baseUrl)}/Account/Logout\" target=\"_top\" rel=\"noopener noreferrer\">Log Out</a>");
sb.AppendLine("<button class=\"wm-btn wm-iframe-hide\" type=\"button\" onclick=\"window.close()\">Close</button>");
sb.AppendLine("<button class=\"wm-btn wm-iframe-only\" type=\"button\" onclick=\"history.back()\">Back</button>");
sb.AppendLine("</div></div>");

sb.AppendLine("<p class=\"wm-meta\">");
sb.Append($"Generated ({Html(tzLabel)}): {Html(FmtLocal(nowUtc, tz))}");
if (lastRunUtc != null)
    sb.Append($" &nbsp;|&nbsp; Last Run ({Html(tzLabel)}): {Html(FmtLocal(lastRunUtc.Value, tz))}");
sb.AppendLine("</p>");

        sb.AppendLine("<table class=\"wm-table\">");
        sb.AppendLine("<thead><tr>");
        sb.AppendLine($"<th>Site</th><th>State</th><th>Since ({Html(tzLabel)})</th><th>Last Check ({Html(tzLabel)})</th><th>Last Summary</th>");
        sb.AppendLine("</tr></thead><tbody>");

        foreach (var t in targets)
        {
            states.TryGetValue(t.TargetId, out var st);

            recentByTarget.TryGetValue(t.TargetId, out var histList);
            var latestChk = (histList != null && histList.Count > 0) ? histList[0] : null;

            var stateText = "Unknown";
            var css = "wm-unknown";

            DateTime? sinceUtc = null;
            DateTime? lastCheckUtc = null;
            var lastSummary = "";

            if (st != null)
            {
                // Same degraded rule as monitor page:
                var degraded = st.IsUp && st.LoginDetectedEver && !st.LoginDetectedLast;

                stateText = st.IsUp ? (degraded ? "Degraded" : "Up") : "Down";
                css = st.IsUp ? (degraded ? "wm-degraded" : "wm-up") : "wm-down";

                sinceUtc = st.StateSinceUtc;
                lastCheckUtc = st.LastCheckUtc;
                lastSummary = st.LastSummary ?? "";
            }
            else if (latestChk != null)
            {
                // Fallback if state row doesn’t exist yet
                lastCheckUtc = latestChk.TimestampUtc;
                lastSummary = latestChk.Summary ?? "";
            }

            var finalUrl = (st?.LastFinalUrl ?? latestChk?.FinalUrl ?? "").Trim();

            // In the per-instance snapshot: Site (line 1) links to the per-target snapshot file
            var perTargetRel = $"targets/{t.TargetId}.html";

            sb.AppendLine($"<tr class=\"{css}\">");
            sb.AppendLine("<td>");
            sb.AppendLine($"<div class=\"wm-site1\"><a class=\"wm-siteLink\" href=\"{HtmlAttr(perTargetRel)}\">{Html(t.Url)}</a></div>");
            var finalUrlHtml = string.IsNullOrWhiteSpace(finalUrl) ? "" : $"<a class=\"wm-finalLink\" href=\"{HtmlAttr(finalUrl)}\" target=\"_blank\" rel=\"noopener noreferrer\">{Html(finalUrl)}</a>";
            sb.AppendLine($"<div class=\"wm-site2\">{finalUrlHtml}</div>");
            sb.AppendLine("</td>");

            sb.AppendLine($"<td>{Html(stateText)}</td>");
            sb.AppendLine($"<td>{(sinceUtc == null ? "" : Html(FmtLocal(sinceUtc.Value, tz)))}</td>");
            sb.AppendLine($"<td>{(lastCheckUtc == null ? "" : Html(FmtLocal(lastCheckUtc.Value, tz)))}</td>");
            sb.AppendLine($"<td>{Html(lastSummary)}</td>");
            sb.AppendLine("</tr>");

            // Also (re)write the per-target snapshot file each tick
            var perTargetPath = Path.Combine(targetsFolder, $"{t.TargetId}.html");
            var perTargetHtml = BuildPerTargetHtml(
                displayName,
                instanceId,
                tzLabel,
                tz,
                baseUrl,
                t.Url,
                finalUrl,
                stateText,
                sinceUtc,
                lastCheckUtc,
                lastSummary,
                histList);

            await AtomicWriteUtf8Async(perTargetPath, perTargetHtml, ct);
        }

        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</div></body></html>");

        var finalPath = Path.Combine(outputFolder, $"{instanceId}.html");
        await AtomicWriteUtf8Async(finalPath, sb.ToString(), ct);
    }

    private static string BuildPerTargetHtml(
        string displayName,
        string instanceId,
        string tzLabel,
        TimeZoneInfo tz,
        string baseUrl,
        string url,
        string finalUrl,
        string stateText,
        DateTime? sinceUtc,
        DateTime? lastCheckUtc,
        string lastSummary,
        List<WebsiteMonitor.Storage.Models.Check>? histList)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
sb.AppendLine("<html lang=\"en\"><head>");
sb.AppendLine("<meta charset=\"utf-8\"/>");
sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"/>");
sb.AppendLine("<meta name=\"color-scheme\" content=\"light dark\"/>");
sb.AppendLine($"<title>ITWebsiteMonitor - Snapshot - {Html(displayName)}</title>");
sb.AppendLine($"<link rel=\"stylesheet\" href=\"{HtmlAttr(baseUrl)}/css/site.css\" />");
sb.AppendLine("<style>.wm-iframe-only{display:none!important} body.wm-in-iframe .wm-iframe-hide{display:none!important} body.wm-in-iframe .wm-iframe-only{display:inline-flex!important}</style>");
sb.AppendLine("<script>(function(){var framed=false;try{framed=window.self!==window.top;}catch(e){framed=true;}if(!framed)return;document.addEventListener(\"DOMContentLoaded\",function(){document.body.classList.add(\"wm-in-iframe\");var t=document.getElementById(\"wm-snap-title\");if(t){var dn=t.getAttribute(\"data-displayname\")||\"\";t.textContent=\"Snapshot - \"+dn;}});})();</script>");
sb.AppendLine("</head><body>");

sb.AppendLine("<div class=\"wm-page\">");
sb.AppendLine("<div class=\"wm-topbar\">");
sb.AppendLine("<div class=\"wm-topbar-title\">");
sb.AppendLine($"<img class=\"wm-logo wm-iframe-hide\" src=\"{HtmlAttr(baseUrl)}/images/itgreatfalls-logo.png\" alt=\"Logo\" />");
sb.AppendLine($"<span id=\"wm-snap-title\" data-displayname=\"{HtmlAttr(displayName)}\">ITWebsiteMonitor - Snapshot - {Html(displayName)}</span>");
sb.AppendLine("</div>");
sb.AppendLine("<div class=\"wm-topbar-actions\">");
sb.AppendLine($"<a class=\"wm-btn wm-iframe-hide\" href=\"{HtmlAttr(baseUrl)}/\">Home</a>");
sb.AppendLine($"<a class=\"wm-btn wm-iframe-hide\" href=\"{HtmlAttr(baseUrl)}/monitor/{HtmlAttr(instanceId)}\">Monitor</a>");
sb.AppendLine($"<a class=\"wm-btn wm-iframe-hide\" href=\"{HtmlAttr(baseUrl)}/setup\">Setup</a>");
sb.AppendLine($"<a class=\"wm-btn wm-iframe-hide\" href=\"{HtmlAttr(baseUrl)}/Account/Logout\" target=\"_top\" rel=\"noopener noreferrer\">Log Out</a>");
sb.AppendLine("<button class=\"wm-btn wm-iframe-hide\" type=\"button\" onclick=\"window.close()\">Close</button>");
sb.AppendLine("<button class=\"wm-btn wm-iframe-only\" type=\"button\" onclick=\"history.back()\">Back</button>");
sb.AppendLine("</div></div>");

sb.AppendLine($"<p class=\"wm-meta\">{Html(url)}</p>");
if (!string.IsNullOrWhiteSpace(finalUrl))
    sb.AppendLine($"<p class=\"wm-meta\"><a class=\"wm-finalLink\" href=\"{HtmlAttr(finalUrl)}\" target=\"_blank\" rel=\"noopener noreferrer\">{Html(finalUrl)}</a></p>");

sb.AppendLine("<table class=\"wm-table\">");
        sb.AppendLine("<thead><tr>");
        sb.AppendLine($"<th>State</th><th>Since ({Html(tzLabel)})</th><th>Last Check ({Html(tzLabel)})</th><th>Last Summary</th>");
        sb.AppendLine("</tr></thead><tbody>");

        var rowClass = "wm-unknown";
if (!string.IsNullOrWhiteSpace(stateText))
{
    var stLower = stateText.Trim().ToLowerInvariant();
    if (stLower.Contains("paused")) rowClass = "wm-paused";
    else if (stLower.Contains("degraded")) rowClass = "wm-degraded";
    else if (stLower.Contains("down")) rowClass = "wm-down";
    else if (stLower.Contains("up")) rowClass = "wm-up";
}

sb.AppendLine($"<tr class=\"{rowClass}\">");
        sb.AppendLine($"<td>{Html(stateText)}</td>");
        sb.AppendLine($"<td>{(sinceUtc == null ? "" : Html(FmtLocal(sinceUtc.Value, tz)))}</td>");
        sb.AppendLine($"<td>{(lastCheckUtc == null ? "" : Html(FmtLocal(lastCheckUtc.Value, tz)))}</td>");
        sb.AppendLine($"<td>{Html(lastSummary)}</td>");
        sb.AppendLine("</tr>");

        sb.AppendLine("</tbody></table>");

        if (histList != null && histList.Count > 0)
        {
            sb.AppendLine("<div class=\"hist\"><strong>Recent checks</strong>");
            sb.AppendLine($"<table><thead><tr><th>{Html(tzLabel)}</th><th>Summary</th></tr></thead><tbody>");

            foreach (var c in histList)
            {
                sb.AppendLine("<tr>");
                sb.AppendLine($"<td>{Html(FmtLocal(c.TimestampUtc, tz))}</td>");
                sb.AppendLine($"<td>{Html(c.Summary ?? "")}</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody></table></div>");
        }

        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    private static async Task AtomicWriteUtf8Async(string finalPath, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var tmpPath = finalPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, content, Encoding.UTF8, ct);
        File.Move(tmpPath, finalPath, true);
    }

    private const string SnapshotDateTimeFormat = "MM/dd/yyyy h:mm:ss tt";

    private static string FmtLocal(DateTime utc, TimeZoneInfo tz)
    {
        // SQLite/EF often comes back as Kind=Unspecified; force UTC semantics
        var asUtc = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(asUtc, tz);
        return local.ToString(SnapshotDateTimeFormat, CultureInfo.InvariantCulture);
    }

    
    private static string NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "http://localhost:5041";

        return baseUrl.Trim().TrimEnd('/');
    }

private static TimeZoneInfo ResolveTimeZone(string? timeZoneId, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZoneInfo.Local;

        var id = timeZoneId.Trim();

        // 1) Direct (works on Linux for IANA; on Windows for Windows IDs)
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            // ignore and try TZConvert
        }

        // 2) Try TimeZoneConverter if present (reflection; no hard dependency)
        try
        {
            var t = Type.GetType("TimeZoneConverter.TZConvert, TimeZoneConverter", false);
            if (t != null)
            {
                var mi = t.GetMethod(
                    "GetTimeZoneInfo",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);

                if (mi != null)
                {
                    var tz = mi.Invoke(null, new object?[] { id }) as TimeZoneInfo;
                    if (tz != null)
                        return tz;
                }
            }
        }
        catch
        {
            // ignore
        }

        logger.LogWarning("Could not resolve TimeZoneId='{TimeZoneId}'. Falling back to Local.", id);
        return TimeZoneInfo.Local;
    }

    private static string Html(string? s)
        => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string HtmlAttr(string? s)
        => System.Net.WebUtility.HtmlEncode(s ?? "");
}
