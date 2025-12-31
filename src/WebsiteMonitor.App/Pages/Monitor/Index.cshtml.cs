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
    public string LastCheckLocal { get; private set; } = "";
    public string TimeZoneLabel { get; private set; } = "";

    public sealed record Row(
        long TargetId,
        string Url,
        string FinalUrl,
        string Tcp,
        string Http,
        string Check,
        string State,          // Up / Down / Unknown / Degraded
        string SinceLocal,     // display TZ
        string DurationSeconds // baseline for the JS timer
    );

    public List<Row> Rows { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var data = await BuildDataAsync(ct);
        if (data == null) return NotFound();

        DisplayName = data.Value.displayName;
        LastCheckLocal = data.Value.lastCheckLocal;
        TimeZoneLabel = data.Value.timeZoneLabel;
        Rows = data.Value.rows;

        return Page();
    }

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
            timeZoneLabel = data.Value.timeZoneLabel,
            nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            lastCheckLocal = data.Value.lastCheckLocal,
            rows = data.Value.rows.Select(r => new
            {
                targetId = r.TargetId,
                url = r.Url,
                finalUrl = r.FinalUrl,
                tcp = r.Tcp,
                http = r.Http,
                check = r.Check,
                state = r.State,
                sinceLocal = r.SinceLocal,
                durationSeconds = r.DurationSeconds
            }).ToList()
        });
    }

    private async Task<(string displayName, string lastCheckLocal, string timeZoneLabel, List<Row> rows)?> BuildDataAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(InstanceId))
            return null;

        var instance = await _db.Instances
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.InstanceId == InstanceId, ct);

        if (instance == null)
            return null;

        var displayTz = ResolveDisplayTimeZone(instance.TimeZoneId, out var tzLabel);

        var nowUtc = DateTime.UtcNow;

        var targets = await _db.Targets
            .AsNoTracking()
            .Where(t => t.InstanceId == InstanceId && t.Enabled)
            .OrderBy(t => t.TargetId)
            .ToListAsync(ct);

        var targetIds = targets.Select(t => t.TargetId).ToList();

        var states = await _db.States
            .AsNoTracking()
            .Where(s => targetIds.Contains(s.TargetId))
            .ToDictionaryAsync(s => s.TargetId, ct);

        var latestTimes = await _db.Checks
            .AsNoTracking()
            .Where(c => targetIds.Contains(c.TargetId))
            .GroupBy(c => c.TargetId)
            .Select(g => new { TargetId = g.Key, Ts = g.Max(x => x.TimestampUtc) })
            .ToListAsync(ct);

        var allChecks = await _db.Checks
            .AsNoTracking()
            .Where(c => targetIds.Contains(c.TargetId))
            .ToListAsync(ct);

        var latestSet = new HashSet<(long TargetId, DateTime Ts)>(
            latestTimes.Select(x => (x.TargetId, x.Ts)));

        var checkMap = allChecks
            .Where(c => latestSet.Contains((c.TargetId, c.TimestampUtc)))
            .GroupBy(c => c.TargetId)
            .Select(g => g.OrderByDescending(x => x.TimestampUtc).First())
            .ToDictionary(c => c.TargetId);

        var lastCheckUtc =
            checkMap.Count == 0
                ? (DateTime?)null
                : checkMap.Values.Max(c => c.TimestampUtc);

        var lastCheckLocal =
            lastCheckUtc == null
                ? ""
                : ToDisplayString(EnsureUtc(lastCheckUtc.Value), displayTz);

        var rows = new List<Row>(targets.Count);

        foreach (var t in targets)
        {
            states.TryGetValue(t.TargetId, out var s);
            checkMap.TryGetValue(t.TargetId, out var chk);

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
                    : ((long)Math.Max(0, (nowUtc - EnsureUtc(s.StateSinceUtc)).TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);

            var sinceLocal =
                s == null
                    ? ""
                    : ToDisplayString(EnsureUtc(s.StateSinceUtc), displayTz);

            var finalUrl = (s?.LastFinalUrl ?? chk?.FinalUrl ?? "").Trim();

            var ip = (s?.LastUsedIp ?? chk?.UsedIp ?? "").Trim();
            var tcpBase =
                chk != null ? (chk.TcpOk ? "OK" : "FAIL")
                : (s?.LastSummary?.Contains("TCP OK", StringComparison.OrdinalIgnoreCase) == true ? "OK"
                   : s?.LastSummary?.Contains("TCP FAIL", StringComparison.OrdinalIgnoreCase) == true ? "FAIL"
                   : "");

            var tcpText =
                string.IsNullOrWhiteSpace(tcpBase) ? ""
                : string.IsNullOrWhiteSpace(ip) ? tcpBase
                : $"{tcpBase} ({ip})";

            var httpText = chk?.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "";

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
                sinceLocal,
                durationSeconds
            ));
        }

        return (instance.DisplayName, lastCheckLocal, tzLabel, rows);
    }

    private static DateTime EnsureUtc(DateTime dt)
        => dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    // CHANGE HERE:
    // Desired display format:
    // - MM/DD/YYYY
    // - 12-hour clock with AM/PM
    // If you truly want MM:DD:YYYY (with colons), change to: "MM':'dd':'yyyy h:mm tt"
    private const string DisplayDateTimeFormat = "MM/dd/yyyy h:mm:ss tt";

    private static string ToDisplayString(DateTime utc, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTimeFromUtc(utc, tz)
            .ToString(DisplayDateTimeFormat, CultureInfo.InvariantCulture);

    private static TimeZoneInfo ResolveDisplayTimeZone(string? instanceTimeZoneId, out string label)
    {
        var serverLocal = TimeZoneInfo.Local;
        label = "ServerLocal";

        if (string.IsNullOrWhiteSpace(instanceTimeZoneId))
            return serverLocal;

        label = instanceTimeZoneId.Trim();

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(label);
        }
        catch
        {
            // try IANA -> Windows
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(label, out var windowsId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }
            catch
            {
                // fall back
            }
        }

        label = "ServerLocal";
        return serverLocal;
    }
}
