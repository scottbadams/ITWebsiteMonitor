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
        DateTime RuntimeChangedUtc);

    public List<Row> Rows { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        // First-run wizard if no instances exist
        var anyInstances = await _db.Instances.AnyAsync();
        if (!anyInstances)
            return Redirect("/setup/first-run");

        var instances = await _db.Instances
            .OrderBy(i => i.InstanceId)
            .ToListAsync();

        Rows = instances.Select(i =>
        {
            _runtime.TryGet(i.InstanceId, out var s);
            return new Row(
                i.InstanceId,
                i.DisplayName,
                i.Enabled,
                s.State.ToString(),
                s.ChangedUtc);
        }).ToList();

        return Page();
    }

    public async Task<IActionResult> OnPostStartAsync(string instanceId)
    {
        await _runtime.StartAsync(instanceId);
        return Redirect("/setup");
    }

    public async Task<IActionResult> OnPostStopAsync(string instanceId)
    {
        await _runtime.StopAsync(instanceId);
        return Redirect("/setup");
    }

    public async Task<IActionResult> OnPostRestartAsync(string instanceId)
    {
        await _runtime.RestartAsync(instanceId);
        return Redirect("/setup");
    }
}
