using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.App.Pages.Monitor;

public sealed class IndexModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;

    public IndexModel(WebsiteMonitorDbContext db)
    {
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public string InstanceId { get; set; } = "";

    public sealed record Row(
        long TargetId,
        bool Enabled,
        string Url,
        string Status,
        string LastCheckUtc,
        string StateSinceUtc,
        int ConsecutiveFailures,
        string LastSummary);

    public List<Row> Rows { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var instanceExists = await _db.Instances.AnyAsync(i => i.InstanceId == InstanceId);
        if (!instanceExists) return NotFound();

        var targets = await _db.Targets
            .Where(t => t.InstanceId == InstanceId)
            .OrderBy(t => t.TargetId)
            .ToListAsync();

        var targetIds = targets.Select(t => t.TargetId).ToList();

        var states = await _db.States
            .Where(s => targetIds.Contains(s.TargetId))
            .ToDictionaryAsync(s => s.TargetId);

        Rows = targets.Select(t =>
        {
            states.TryGetValue(t.TargetId, out var s);

            var status = s == null ? "Unknown" : (s.IsUp ? "UP" : "DOWN");
            return new Row(
                t.TargetId,
                t.Enabled,
                t.Url,
                status,
                s?.LastCheckUtc.ToString("u") ?? "",
                s?.StateSinceUtc.ToString("u") ?? "",
                s?.ConsecutiveFailures ?? 0,
                s?.LastSummary ?? "");
        }).ToList();

        return Page();
    }
}
