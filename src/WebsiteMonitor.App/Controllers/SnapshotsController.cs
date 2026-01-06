using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using WebsiteMonitor.App.Infrastructure;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.App.Controllers;

[ApiController]
public sealed class SnapshotsController : ControllerBase
{
    private readonly WebsiteMonitorDbContext _db;
    private readonly ProductPaths _paths;

    public SnapshotsController(WebsiteMonitorDbContext db, ProductPaths paths)
    {
        _db = db;
        _paths = paths;
    }

    // GET /snapshots/{instanceId}/{targetId}
    [HttpGet("/snapshots/{instanceId}/{targetId:long}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetTargetSnapshot(string instanceId, long targetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return NotFound();

        var inst = await _db.Instances
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.InstanceId == instanceId, ct);

        if (inst == null || string.IsNullOrWhiteSpace(inst.OutputFolder))
            return NotFound();

        var outputFolder = Path.IsPathRooted(inst.OutputFolder!)
            ? inst.OutputFolder!
            : Path.Combine(_paths.DataRoot, inst.OutputFolder!);

        var rootFull = Path.GetFullPath(outputFolder);
        var fileFull = Path.GetFullPath(Path.Combine(outputFolder, "targets", $"{targetId}.html"));

        // basic containment check
        if (!fileFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (!System.IO.File.Exists(fileFull))
            return NotFound();

        var html = await System.IO.File.ReadAllTextAsync(fileFull, ct);

        // Force dark theme if the authenticated user has it enabled (overrides query scheme).
        var userTheme = User?.FindFirst("wm:theme")?.Value;
        var isUserForcedDark = string.Equals(userTheme, "dark", StringComparison.OrdinalIgnoreCase);

        var schemeParamRaw = (Request.Query["scheme"].ToString() ?? "").Trim();
        string effectiveTheme;
        if (isUserForcedDark)
        {
            effectiveTheme = "dark";
        }
        else if (string.Equals(schemeParamRaw, "dark", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(schemeParamRaw, "light", StringComparison.OrdinalIgnoreCase))
        {
            effectiveTheme = schemeParamRaw.ToLowerInvariant();
        }
        else
        {
            effectiveTheme = "light";
        }

        html = ApplyTheme(html, effectiveTheme);
        return Content(html, "text/html; charset=utf-8");
    }

    private static string ApplyTheme(string html, string effectiveTheme)
    {
        var addClass = string.Equals(effectiveTheme, "dark", StringComparison.OrdinalIgnoreCase) ? "theme-dark" : "theme-light";
        var removeClass = string.Equals(effectiveTheme, "dark", StringComparison.OrdinalIgnoreCase) ? "theme-light" : "theme-dark";

        // Ensure body has the correct theme class.
        var bodyRx = new Regex("<body(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        html = bodyRx.Replace(html, m =>
        {
            var attrs = m.Groups["attrs"].Value;
            var classRx = new Regex("\\bclass\\s*=\\s*\"(?<c>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (classRx.IsMatch(attrs))
            {
                attrs = classRx.Replace(attrs, cm =>
                {
                    var existing = cm.Groups["c"].Value;
                    var parts = existing.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => !string.Equals(p, removeClass, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (!parts.Any(p => string.Equals(p, addClass, StringComparison.OrdinalIgnoreCase)))
                        parts.Add(addClass);
                    var merged = string.Join(" ", parts);
                    return $"class=\"{merged}\"";
                });
            }
            else
            {
                attrs = attrs + $" class=\"{addClass}\"";
            }
            return $"<body{attrs}>";
        }, 1);

        // Align the meta color-scheme to the selected theme (helps form controls + prevents auto-restyling).
        var metaValue = string.Equals(effectiveTheme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
        var metaRx = new Regex("<meta\\s+name=\"color-scheme\"[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (metaRx.IsMatch(html))
        {
            html = metaRx.Replace(html, $"<meta name=\"color-scheme\" content=\"{metaValue}\" />", 1);
        }

        return html;
    }
}
