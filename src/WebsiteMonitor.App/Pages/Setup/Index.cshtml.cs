using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Monitoring.Runtime;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.App.Pages.Setup;

public sealed class IndexModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;
    private readonly IInstanceRuntimeManager _runtime;

    public IndexModel(WebsiteMonitorDbContext db, IInstanceRuntimeManager runtime)
    {
        _db = db;
        _runtime = runtime;
    }

    public sealed record Row(
        string InstanceId,
        string DisplayName,
        bool Enabled,
        string RuntimeState,
        DateTime RuntimeChangedUtc
    );

    public List<Row> Rows { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var instances = await _db.Instances
            .AsNoTracking()
            .OrderBy(i => i.DisplayName)
            .ThenBy(i => i.InstanceId)
            .ToListAsync(ct);

        var rows = new List<Row>(instances.Count);
        foreach (var i in instances)
        {
            if (!_runtime.TryGet(i.InstanceId, out var st))
            {
                // When the runtime status doesn't exist yet, treat it as "Paused".
                st = new InstanceRuntimeStatus(
                    i.InstanceId,
                    InstanceRunState.Paused,
                    DateTime.UtcNow,
                    "Not created");
            }

            rows.Add(new Row(
                i.InstanceId,
                i.DisplayName ?? "",
                i.Enabled,
                st.State.ToString(),
                st.ChangedUtc
            ));
        }

        Rows = rows;
    }

    public async Task<IActionResult> OnPostStartAsync([FromForm] string instanceId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            await _runtime.StartAsync(instanceId, ct);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStopAsync([FromForm] string instanceId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            await _runtime.StopAsync(instanceId, ct);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestartAsync([FromForm] string instanceId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            await _runtime.RestartAsync(instanceId, ct);
        }
        return RedirectToPage();
    }

    public sealed record ToggleEnabledRequest(string InstanceId, bool Enabled);

    // POST /setup?handler=ToggleEnabled (AJAX)
    public async Task<IActionResult> OnPostToggleEnabledAsync([FromBody] ToggleEnabledRequest req, CancellationToken ct)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.InstanceId))
        {
            return new JsonResult(new { ok = false, error = "instanceId is required." }) { StatusCode = 400 };
        }

        var inst = await _db.Instances.SingleOrDefaultAsync(i => i.InstanceId == req.InstanceId, ct);
        if (inst == null)
        {
            return new JsonResult(new { ok = false, error = "Instance not found." }) { StatusCode = 404 };
        }

        var enabledChanged = inst.Enabled != req.Enabled;
        inst.Enabled = req.Enabled;
        await _db.SaveChangesAsync(ct);

        // If disabling, stop runtime so monitoring actually stops.
        // When enabling, we do NOT auto-start (consistent with per-instance Save behavior).
        if (enabledChanged && !inst.Enabled)
        {
            try { await _runtime.StopAsync(inst.InstanceId, ct); }
            catch { /* runtime stop is best-effort */ }
        }

        if (!_runtime.TryGet(inst.InstanceId, out var st))
        {
            st = new InstanceRuntimeStatus(
                inst.InstanceId,
                InstanceRunState.Paused,
                DateTime.UtcNow,
                "Not created");
        }

        return new JsonResult(new
        {
            ok = true,
            instanceId = inst.InstanceId,
            enabled = inst.Enabled,
            runtimeState = st.State.ToString(),
            runtimeChangedUtc = st.ChangedUtc.ToString("u")
        });
    }
}
