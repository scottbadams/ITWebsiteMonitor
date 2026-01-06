using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.App.Pages.Setup.Groups;

[Authorize(Roles = "Admin")]
public sealed class IndexModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;

    public IndexModel(WebsiteMonitorDbContext db)
    {
        _db = db;
    }

    public string? Error { get; private set; }

    public List<GroupRow> Groups { get; private set; } = new();

    public sealed record GroupRow(int Id, string Name, bool Enabled, int MemberCount);

    public async Task OnGetAsync()
    {
        try
        {
            Groups = await _db.Groups.AsNoTracking()
                .OrderBy(g => g.Name)
                .Select(g => new GroupRow(
                    g.Id,
                    g.Name,
                    g.Enabled,
                    _db.GroupMembers.Count(m => m.GroupId == g.Id)
                ))
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message + " (Did you run the new EF migration?)";
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var g = await _db.Groups.FirstOrDefaultAsync(x => x.Id == id);
        if (g is null) return RedirectToPage();

        g.Enabled = !g.Enabled;
        await _db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var g = await _db.Groups.FirstOrDefaultAsync(x => x.Id == id);
        if (g is null) return RedirectToPage();

        _db.Groups.Remove(g);
        await _db.SaveChangesAsync();
        return RedirectToPage();
    }
}
