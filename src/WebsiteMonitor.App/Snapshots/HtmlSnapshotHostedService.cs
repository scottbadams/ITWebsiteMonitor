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

        var instances = await db.Instances
            .Where(i => i.Enabled && i.WriteHtmlSnapshot && i.OutputFolder != null && i.OutputFolder != "")
            .ToListAsync(ct);

        foreach (var inst in instances)
        {
            try
            {
                await WriteOneInstanceSnapshotAsync(
                    db,
                    inst.InstanceId,
                    inst.DisplayName,
                    inst.OutputFolder!,
                    ct);
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
        string outputFolderRaw,
        CancellationToken ct)
    {
        // Relative -> under DataRoot. Absolute -> use as-is.
        var outputFolder = Path.IsPathRooted(outputFolderRaw)
            ? outputFolderRaw
            : Path.Combine(_paths.DataRoot, outputFolderRaw);

        Directory.CreateDirectory(outputFolder);

        var targets = await db.Targets
            .Where(t => t.InstanceId == instanceId && t.Enabled)
            .OrderBy(t => t.Url)
            .ToListAsync(ct);

        if (targets.Count == 0)
            return;

        var targetIds = targets.Select(t => t.TargetId).ToList();

        var states = await db.States
            .Where(s => targetIds.Contains(s.TargetId))
            .ToDictionaryAsync(s => s.TargetId, ct);

        // Pull a bounded set of recent checks across all targets, then group in-memory.
        var perTargetN = Math.Max(1, _opt.HistoryCount);
        var maxChecks = perTargetN * targets.Count;

        var recentChecks = await db.Checks
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

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"/>");
        sb.AppendLine($"<title>WebsiteMonitor - {Html(displayName)} ({Html(instanceId)})</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:16px;background:#111;color:#eee;}");
        sb.AppendLine("h1{margin:0 0 6px 0;font-size:20px;}");
        sb.AppendLine(".meta{color:#bbb;margin:0 0 12px 0;font-size:12px;}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;}");
        sb.AppendLine("th,td{border:1px solid #333;padding:8px;vertical-align:top;}");
        sb.AppendLine("th{background:#1b1b1b;text-align:left;}");
        sb.AppendLine(".up{background:#0f2a16;}");
        sb.AppendLine(".down{background:#2a0f0f;}");
        sb.AppendLine(".unknown{background:#1a1a1a;}");
        sb.AppendLine(".url2{color:#aaa;font-size:12px;margin-top:4px;}");
        sb.AppendLine(".hist{margin-top:6px;font-size:12px;color:#ddd;}");
        sb.AppendLine(".hist table{width:100%;}");
        sb.AppendLine(".hist th,.hist td{padding:4px 6px;}");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine($"<h1>WebsiteMonitor - {Html(displayName)} ({Html(instanceId)})</h1>");
        sb.Append("<div class=\"meta\">");
        sb.Append($"Generated (UTC): {nowUtc:u}");
        if (lastRunUtc != null)
            sb.Append($" &nbsp;|&nbsp; Last Run (UTC): {lastRunUtc.Value:u}");
        sb.AppendLine("</div>");

        sb.AppendLine("<table>");
        sb.AppendLine("<thead><tr>");
        sb.AppendLine("<th>Site</th><th>State</th><th>Since (UTC)</th><th>Last Check (UTC)</th><th>Last Summary</th>");
        sb.AppendLine("</tr></thead><tbody>");

        foreach (var t in targets)
        {
            states.TryGetValue(t.TargetId, out var st);

            var stateText = "Unknown";
            var css = "unknown";
            DateTime? sinceUtc = null;
            DateTime? lastCheckUtc = null;
            var lastSummary = "";

            if (st != null)
            {
                stateText = st.IsUp ? "Up" : "Down";
                css = st.IsUp ? "up" : "down";
                sinceUtc = st.StateSinceUtc;
                lastCheckUtc = st.LastCheckUtc;
                lastSummary = st.LastSummary ?? "";
            }

            sb.AppendLine($"<tr class=\"{css}\">");
            sb.AppendLine("<td>");
            sb.AppendLine($"<div>{Html(t.Url)}</div>");
            // Final URL after redirects is a Step 9 enhancement; leave blank in Step 8.
            sb.AppendLine("<div class=\"url2\"></div>");
            sb.AppendLine("</td>");

            sb.AppendLine($"<td>{Html(stateText)}</td>");
            sb.AppendLine($"<td>{(sinceUtc == null ? "" : sinceUtc.Value.ToString("u"))}</td>");
            sb.AppendLine($"<td>{(lastCheckUtc == null ? "" : lastCheckUtc.Value.ToString("u"))}</td>");
            sb.AppendLine($"<td>{Html(lastSummary)}</td>");
            sb.AppendLine("</tr>");

            if (recentByTarget.TryGetValue(t.TargetId, out var hist) && hist.Count > 0)
            {
                sb.AppendLine("<tr><td colspan=\"5\">");
                sb.AppendLine("<div class=\"hist\"><strong>Recent checks</strong>");
                sb.AppendLine("<table><thead><tr><th>UTC</th><th>Summary</th></tr></thead><tbody>");

                foreach (var c in hist)
                {
                    sb.AppendLine("<tr>");
                    sb.AppendLine($"<td>{c.TimestampUtc:u}</td>");
                    sb.AppendLine($"<td>{Html(c.Summary ?? "")}</td>");
                    sb.AppendLine("</tr>");
                }

                sb.AppendLine("</tbody></table></div>");
                sb.AppendLine("</td></tr>");
            }
        }

        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</body></html>");

        var finalPath = Path.Combine(outputFolder, $"{instanceId}.html");
        var tmpPath = finalPath + ".tmp";

        await File.WriteAllTextAsync(tmpPath, sb.ToString(), Encoding.UTF8, ct);
        File.Move(tmpPath, finalPath, true);
    }

    private static string Html(string s)
        => System.Net.WebUtility.HtmlEncode(s ?? "");
}
