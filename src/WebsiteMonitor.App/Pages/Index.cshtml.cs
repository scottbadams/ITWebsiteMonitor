using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Monitoring.Runtime;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Identity;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.App.Pages;

public sealed class IndexModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IInstanceRuntimeManager _runtime;

    public IndexModel(
        WebsiteMonitorDbContext db,
        SignInManager<ApplicationUser> signInManager,
        IInstanceRuntimeManager runtime)
    {
        _db = db;
        _signInManager = signInManager;
        _runtime = runtime;
    }

    public List<Instance> Instances { get; private set; } = new();

    public bool IsSignedIn => _signInManager.IsSignedIn(User);

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!IsSignedIn) return;

        Instances = await _db.Instances
            .AsNoTracking()
            .OrderBy(i => i.DisplayName)
            .ThenBy(i => i.InstanceId)
            .ToListAsync(ct);
    }

    public async Task<IActionResult> OnGetOverviewAsync(CancellationToken ct)
    {
        if (!IsSignedIn) return Unauthorized();

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        var data = await BuildOverviewDataAsync(ct);
        return new JsonResult(data);
    }

    private async Task<object> BuildOverviewDataAsync(CancellationToken ct)
    {
        // Global defaults (per your clarification)
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == 1, ct);

        var globalInterval = settings?.DefaultCheckIntervalSeconds ?? 60;
        var globalConcurrency = settings?.DefaultConcurrencyLimit ?? 20;

        // Enabled instances only (per requirement)
        var enabledInstances = await _db.Instances
            .AsNoTracking()
            .Where(i => i.Enabled)
            .OrderBy(i => i.DisplayName)
            .ThenBy(i => i.InstanceId)
            .ToListAsync(ct);

        if (enabledInstances.Count == 0)
        {
            return new
            {
                lastCycleLocal = "",
                rows = Array.Empty<object>()
            };
        }

        var instanceIds = enabledInstances.Select(i => i.InstanceId).ToList();

        var targets = await _db.Targets
            .AsNoTracking()
            .Where(t => instanceIds.Contains(t.InstanceId) && t.Enabled)
            .Select(t => new { t.TargetId, t.InstanceId })
            .ToListAsync(ct);

        var targetsByInstance = targets
            .GroupBy(t => t.InstanceId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.TargetId).ToList());

        var targetIds = targets.Select(t => t.TargetId).ToList();

        Dictionary<long, TargetState> stateMap = new();
        Dictionary<long, DateTime> latestCheckMap = new();

        if (targetIds.Count > 0)
        {
            stateMap = await _db.States
                .AsNoTracking()
                .Where(s => targetIds.Contains(s.TargetId))
                .ToDictionaryAsync(s => s.TargetId, ct);

            var latestTimes = await _db.Checks
                .AsNoTracking()
                .Where(c => targetIds.Contains(c.TargetId))
                .GroupBy(c => c.TargetId)
                .Select(g => new { TargetId = g.Key, Ts = g.Max(x => x.TimestampUtc) })
                .ToListAsync(ct);

            latestCheckMap = latestTimes.ToDictionary(x => x.TargetId, x => EnsureUtc(x.Ts));
        }

        // "End of the last cycle run" (best available proxy): newest check timestamp across enabled instances.
        var globalLastUtc = latestCheckMap.Count == 0 ? (DateTime?)null : latestCheckMap.Values.Max();

        var globalTz = ResolveDisplayTimeZone((settings?.DefaultTimeZoneId ?? "America/Phoenix").Trim(), out _);
        var lastCycleLocal = globalLastUtc == null ? "" : ToDisplayString(globalLastUtc.Value, globalTz);

        var rtMap = _runtime
            .GetAll()
            .ToDictionary(x => x.InstanceId, StringComparer.OrdinalIgnoreCase);

        var rows = new List<object>(enabledInstances.Count);

        foreach (var inst in enabledInstances)
        {
            rtMap.TryGetValue(inst.InstanceId, out var rt);
            var isPaused = rt == null || rt.State == InstanceRunState.Paused;

            var tids = targetsByInstance.TryGetValue(inst.InstanceId, out var list) ? list : new List<long>();
            var targetsEnabled = tids.Count;

            var anyDown = false;
            var anyYellow = false;
            var anyUnknown = false;

            if (targetsEnabled == 0)
            {
                anyUnknown = true;
            }

            if (isPaused)
            {
                anyYellow = true; // paused counts as yellow
            }

            foreach (var tid in tids)
            {
                if (!stateMap.TryGetValue(tid, out var s))
                {
                    anyUnknown = true;
                    continue;
                }

                if (!s.IsUp)
                {
                    anyDown = true;
                    break;
                }

                var degraded = s.IsUp && s.LoginDetectedEver && !s.LoginDetectedLast;
                if (degraded) anyYellow = true;
            }

            string statusText;
            string rowClass;

            // Worst-status priority: Red > Yellow > Grey > Green
            if (anyDown)
            {
                statusText = "Down";
                rowClass = "wm-down";
            }
            else if (anyYellow)
            {
                statusText = isPaused ? "Paused" : "Degraded";
                rowClass = isPaused ? "wm-paused" : "wm-degraded";
            }
            else if (anyUnknown)
            {
                statusText = "Unknown";
                rowClass = "wm-unknown";
            }
            else
            {
                statusText = "Up";
                rowClass = "wm-up";
            }

            DateTime? lastCheckUtc = null;
            if (tids.Count > 0)
            {
                var times = tids
                    .Where(tid => latestCheckMap.ContainsKey(tid))
                    .Select(tid => latestCheckMap[tid])
                    .ToList();

                if (times.Count > 0)
                    lastCheckUtc = times.Max();
            }

            var tz = ResolveDisplayTimeZone((inst.TimeZoneId ?? "").Trim(), out _);
            var lastCheckLocal = lastCheckUtc == null ? "" : ToDisplayString(lastCheckUtc.Value, tz);

            rows.Add(new
            {
                instanceId = inst.InstanceId,
                displayName = inst.DisplayName,
                targetsEnabled,
                checkIntervalSeconds = (inst.CheckIntervalSeconds > 0 ? inst.CheckIntervalSeconds : globalInterval),
                concurrencyLimit = (inst.ConcurrencyLimit > 0 ? inst.ConcurrencyLimit : globalConcurrency),
                lastCheckLocal,
                statusText,
                rowClass
            });
        }

        return new
        {
            lastCycleLocal,
            rows
        };
    }

    private static DateTime EnsureUtc(DateTime dt)
        => dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    private const string DisplayFormat = "MM/dd/yyyy h:mm:ss tt";

    private static string ToDisplayString(DateTime utc, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(utc), tz).ToString(DisplayFormat, CultureInfo.InvariantCulture);

    private static TimeZoneInfo ResolveDisplayTimeZone(string? id, out string label)
    {
        var serverLocal = TimeZoneInfo.Local;

        label = (id ?? "").Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            label = "ServerLocal";
            return serverLocal;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(label);
        }
        catch
        {
            // keep going
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
