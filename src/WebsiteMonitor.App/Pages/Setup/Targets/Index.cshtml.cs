using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.App.Pages.Setup.Targets;

public sealed class IndexModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;

    public IndexModel(WebsiteMonitorDbContext db)
    {
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public string InstanceId { get; set; } = "";

    public sealed record Row(long TargetId, bool Enabled, string Url, string? LoginRule, int? HttpMin, int? HttpMax);
    public List<Row> Rows { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        // Ensure instance exists
        var instanceExists = await _db.Instances.AnyAsync(i => i.InstanceId == InstanceId);
        if (!instanceExists) return NotFound();

        var targets = await _db.Targets
            .Where(t => t.InstanceId == InstanceId)
            .OrderBy(t => t.TargetId)
            .ToListAsync();

        Rows = targets.Select(t => new Row(t.TargetId, t.Enabled, t.Url, t.LoginRule, t.HttpExpectedStatusMin, t.HttpExpectedStatusMax)).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(long targetId)
    {
        var t = await _db.Targets.SingleOrDefaultAsync(x => x.TargetId == targetId && x.InstanceId == InstanceId);
        if (t == null) return NotFound();

        t.Enabled = !t.Enabled;
        await _db.SaveChangesAsync();

        return Redirect($"/setup/instances/{InstanceId}/targets");
    }

    public async Task<IActionResult> OnPostDeleteAsync(long targetId)
    {
        var t = await _db.Targets.SingleOrDefaultAsync(x => x.TargetId == targetId && x.InstanceId == InstanceId);
        if (t == null) return NotFound();

        _db.Targets.Remove(t);
        await _db.SaveChangesAsync();

        return Redirect($"/setup/instances/{InstanceId}/targets");
    }
}
