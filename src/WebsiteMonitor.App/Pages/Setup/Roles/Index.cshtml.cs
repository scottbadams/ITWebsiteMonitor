using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace WebsiteMonitor.App.Pages.Setup.Roles;

[Authorize(Roles = "Admin")]
public sealed class IndexModel : PageModel
{
    private readonly RoleManager<IdentityRole> _roles;

    public IndexModel(RoleManager<IdentityRole> roles)
    {
        _roles = roles;
    }

    public string? Error { get; private set; }

    public List<string> Roles { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Roles = await _roles.Roles.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => r.Name ?? "")
            .Where(n => n != "")
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return RedirectToPage();

        var res = await _roles.CreateAsync(new IdentityRole(name.Trim()));
        if (!res.Succeeded)
        {
            Error = string.Join("; ", res.Errors.Select(e => e.Description));
            await OnGetAsync();
            return Page();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string name)
    {
        var role = await _roles.FindByNameAsync(name);
        if (role is null) return RedirectToPage();

        var res = await _roles.DeleteAsync(role);
        if (!res.Succeeded)
        {
            Error = string.Join("; ", res.Errors.Select(e => e.Description));
            await OnGetAsync();
            return Page();
        }

        return RedirectToPage();
    }
}
