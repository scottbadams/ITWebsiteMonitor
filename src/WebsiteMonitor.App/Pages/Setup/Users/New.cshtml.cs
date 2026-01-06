using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebsiteMonitor.Storage.Identity;

namespace WebsiteMonitor.App.Pages.Setup.Users;

[Authorize(Roles = "Admin")]
public sealed class NewModel : PageModel
{
    private readonly UserManager<ApplicationUser> _users;

    public NewModel(UserManager<ApplicationUser> users)
    {
        _users = users;
    }

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string? FullName { get; set; }
    [BindProperty] public string? PhoneNumber { get; set; }
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool DarkThemeEnabled { get; set; }

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Email))
        {
            Error = "Email is required.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Password) || Password.Length < 8)
        {
            Error = "Password must be at least 8 characters.";
            return Page();
        }

        var user = new ApplicationUser
        {
            UserName = Email.Trim(),
            Email = Email.Trim(),
            FullName = string.IsNullOrWhiteSpace(FullName) ? null : FullName.Trim(),
            PhoneNumber = string.IsNullOrWhiteSpace(PhoneNumber) ? null : PhoneNumber.Trim(),
            DarkThemeEnabled = DarkThemeEnabled,
        };

        var res = await _users.CreateAsync(user, Password);
        if (!res.Succeeded)
        {
            Error = string.Join("; ", res.Errors.Select(e => e.Description));
            return Page();
        }

        return RedirectToPage("/Setup/Users/Index");
    }
}
