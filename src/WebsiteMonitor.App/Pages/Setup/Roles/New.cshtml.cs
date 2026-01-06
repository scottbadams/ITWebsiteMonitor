using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebsiteMonitor.App.Pages.Setup.Roles;

[Authorize(Roles = "Admin")]
public sealed class NewModel : PageModel
{
    private readonly RoleManager<IdentityRole> _roles;

    public NewModel(RoleManager<IdentityRole> roles)
    {
        _roles = roles;
    }

    [BindProperty]
    [Required]
    [MinLength(2)]
    public string Name { get; set; } = "";

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var name = Name.Trim();
        var res = await _roles.CreateAsync(new IdentityRole(name));
        if (!res.Succeeded)
        {
            foreach (var e in res.Errors)
                ModelState.AddModelError("", e.Description);
            return Page();
        }

        return RedirectToPage("/Setup/Roles/Index");
    }
}
