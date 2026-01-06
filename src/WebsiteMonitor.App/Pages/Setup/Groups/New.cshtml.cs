using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.App.Pages.Setup.Groups;

[Authorize(Roles = "Admin")]
public sealed class NewModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;

    public NewModel(WebsiteMonitorDbContext db)
    {
        _db = db;
    }

    [BindProperty] public string Name { get; set; } = "";
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "Name is required.";
            return Page();
        }

        var exists = await _db.Groups.AnyAsync(g => g.Name.ToLower() == Name.Trim().ToLower());
        if (exists)
        {
            Error = "A group with this name already exists.";
            return Page();
        }

        _db.Groups.Add(new Group { Name = Name.Trim(), Enabled = true, CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        return RedirectToPage("/Setup/Groups/Index");
    }
}
