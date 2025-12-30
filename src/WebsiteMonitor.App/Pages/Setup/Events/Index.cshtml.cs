using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.App.Pages.Setup.Events;

[Authorize(Roles = "Admin")]
public sealed class IndexModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;
    public IndexModel(WebsiteMonitorDbContext db) => _db = db;

    public sealed record Row(string TimestampUtc, string InstanceId, string TargetId, string Type, string Message);
    public List<Row> Rows { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var evts = await _db.Events
            .OrderByDescending(e => e.TimestampUtc)
            .Take(200)
            .ToListAsync();

        Rows = evts.Select(e => new Row(
            e.TimestampUtc.ToString("u"),
            e.InstanceId,
            e.TargetId?.ToString() ?? "",
            e.Type,
            e.Message)).ToList();
    }
}
