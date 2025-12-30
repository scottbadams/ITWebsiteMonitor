using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.App.Pages.Monitor;

public sealed class IndexModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;

    public IndexModel(WebsiteMonitorDbContext db) => _db = db;

    [BindProperty(SupportsGet = true)]
    public string InstanceId { get; set; } = "";

    public string DisplayName { get; private set; } = "";
    public string LastCheckUtc { get; private set; } = "";

    public sealed record Row(
        long TargetId,
        string Url,
        string FinalUrl,
        string Tcp,
        string Http,
        string Check,
        string State,          // Up / Down / Unknown / Degraded
        string SinceUtc,       // yyyy-MM-dd HH:mm:ss (UTC)
        string DurationSeconds // baseline for the JS timer
    );

    public List<Row> Rows { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var data = await BuildDataAsync(ct);
        if (data == null) return NotFound();

        DisplayName = data.Value.displayName;
        LastCheckUtc = data.Value.lastCheckUtc;
        Rows = data.Value.rows;

        return Page();
    }

    // JSON polling endpoint:
    // GET /monitor/{instanceId}?handler=Data
    public async Task<IActionResult> OnGetDataAsync(CancellationToken ct)
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        var data = await BuildDataAsync(ct);
        if (data == null) return NotFound();

        return new JsonResult(new
        {
            instanceId = InstanceId,
            displayName = data.Value.displayName,
            nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            lastCheckUtc = data.Value.lastCheckUtc,
            rows = data.Value.rows.Select(r => new
            {
                targetId = r.TargetId,
                url = r.Url,
                finalUrl = r.FinalUrl,
                tcp = r.Tcp,
                http = r.Http,
                check = r.Check,
                state = r.State,
                sinceUtc = r.SinceUtc,
                durationSeconds = r.DurationSeconds
            })
        });
    }

    private async Task<(string displayName, string lastCheckUtc, List<Row> rows)?> BuildDataAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(InstanceId))
            return null;

        var instance = await _db.Instances
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.InstanceId == InstanceId, ct);

        if (instance == null)
            return null;

        var nowUtc = DateTime.UtcNow;

        var targets = await _db.Targets
            .AsNoTracking()
            .Where(t => t.InstanceId == InstanceId && t.Enabled)
            .OrderBy(t => t.TargetId)
            .ToListAsync(ct);

        if (targets.Count == 0)
            return (instance.DisplayName, "", new List<Row>());

        var targetIds = targets.Select(t => t.TargetId).ToList();

        var states = await _db.States
            .AsNoTracking()
            .Where(s => targetIds.Contains(s.TargetId))
            .ToDictionaryAsync(s => s.TargetId, ct);

        // Latest check per target - KEEP THIS AS A SERVER-TRANSLATABLE QUERY (no ToList before join)
        var latestTsQuery =
            from c in _db.Checks.AsNoTracking()
            where targetIds.Contains(c.TargetId)
            group c by c.TargetId into g
            select new { TargetId = g.Key, Ts = g.Max(x => x.TimestampUtc) };

        var latestChecks = await (
            from c in _db.Checks.AsNoTracking()
            join lt in latestTsQuery
                on new { c.TargetId, c.TimestampUtc }
                equals new { lt.TargetId, TimestampUtc = lt.Ts }
            select c
        ).ToListAsync(ct);

        var checkMap = latestChecks.ToDictionary(c => c.TargetId);

        var lastCheckUtc =
            latestChecks.Count == 0
                ? ""
                : latestChecks.Max(c => c.TimestampUtc).ToString("u");

        var rows = new List<Row>(targets.Count);

        foreach (var t in targets)
        {
            states.TryGetValue(t.TargetId, out var s);
            checkMap.TryGetValue(t.TargetId, out var chk);

            // Degraded rule:
            // if currently UP, and login was ever detected, but not detected on last check => Degraded
            var degraded =
                s != null &&
                s.IsUp &&
                s.LoginDetectedEver &&
                !s.LoginDetectedLast;

            var stateText =
                s == null ? "Unknown" :
                !s.IsUp ? "Down" :
                degraded ? "Degraded" :
                "Up";

            var durationSeconds =
                s == null
                    ? "0"
                    : ((long)Math.Max(0, (nowUtc - s.StateSinceUtc).TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);

            var sinceUtc =
                s == null
                    ? ""
                    : s.StateSinceUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            var finalUrl = (s?.LastFinalUrl ?? chk?.FinalUrl ?? "").Trim();

            // TCP column: OK/FAIL + (IP) if we have it
            var ip = (s?.LastUsedIp ?? chk?.UsedIp ?? "").Trim();
            var tcpBase = chk == null ? "" : (chk.TcpOk ? "OK" : "FAIL");
            var tcpText =
                string.IsNullOrWhiteSpace(tcpBase) ? ""
                : string.IsNullOrWhiteSpace(ip) ? tcpBase
                : $"{tcpBase} ({ip})";

            var httpText = chk?.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "";

            // Check column: detected login type; default to Generic
            var checkText = (s?.LastDetectedLoginType ?? chk?.DetectedLoginType ?? "").Trim();
            if (string.IsNullOrWhiteSpace(checkText))
                checkText = "Generic";

            rows.Add(new Row(
                t.TargetId,
                t.Url,
                finalUrl,
                tcpText,
                httpText,
                checkText,
                stateText,
                sinceUtc,
                durationSeconds
            ));
        }

        return (instance.DisplayName, lastCheckUtc, rows);
    }
}
