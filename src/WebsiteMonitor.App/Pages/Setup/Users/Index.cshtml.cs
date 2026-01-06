using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Identity;

namespace WebsiteMonitor.App.Pages.Setup.Users;

[Authorize(Roles = "Admin")]
public sealed class IndexModel : PageModel
{
    private readonly UserManager<ApplicationUser> _users;

    public IndexModel(UserManager<ApplicationUser> users)
    {
        _users = users;
    }

    public string? Error { get; private set; }

    public List<UserRow> Users { get; private set; } = new();

    public sealed record UserRow(string Id, string Email, string? FullName, string? PhoneNumber, bool DarkThemeEnabled);

    public async Task OnGetAsync()
    {
        try
        {
            Users = await _users.Users.AsNoTracking()
                .OrderBy(u => u.Email)
                .Select(u => new UserRow(u.Id, u.Email ?? "", u.FullName, u.PhoneNumber, u.DarkThemeEnabled))
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        var u = await _users.FindByIdAsync(id);
        if (u is null) return RedirectToPage();

        var res = await _users.DeleteAsync(u);
        if (!res.Succeeded)
        {
            TempData["wm_error"] = string.Join("; ", res.Errors.Select(e => e.Description));
        }
        return RedirectToPage();
    }
}
