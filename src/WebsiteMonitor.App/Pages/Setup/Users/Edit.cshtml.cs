using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebsiteMonitor.Storage.Identity;

namespace WebsiteMonitor.App.Pages.Setup.Users;

[Authorize(Roles = "Admin")]
public sealed class EditModel : PageModel
{
    private readonly UserManager<ApplicationUser> _users;

    public EditModel(UserManager<ApplicationUser> users)
    {
        _users = users;
    }

    [BindProperty(SupportsGet = true)] public string Id { get; set; } = "";

    public string? Error { get; private set; }

    public UserDto? UserEntity { get; private set; }

    public sealed record UserDto(string Id, string Email, string? FullName, string? PhoneNumber, bool DarkThemeEnabled);

    public async Task OnGetAsync()
    {
        var u = await _users.FindByIdAsync(Id);
        if (u is null) return;

        UserEntity = new UserDto(u.Id, u.Email ?? "", u.FullName, u.PhoneNumber, u.DarkThemeEnabled);
    }

    public async Task<IActionResult> OnPostAsync(string id, string email, string? fullName, string? phoneNumber, bool darkThemeEnabled)
    {
        var u = await _users.FindByIdAsync(id);
        if (u is null) return RedirectToPage("/Setup/Users/Index");

        if (string.IsNullOrWhiteSpace(email))
        {
			Error = "Email is required.";
			Id = id;
			await OnGetAsync();
			return Page();
        }

        u.Email = email.Trim();
        u.UserName = email.Trim();
        u.FullName = string.IsNullOrWhiteSpace(fullName) ? null : fullName.Trim();
        u.PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();
        u.DarkThemeEnabled = darkThemeEnabled;

        var res = await _users.UpdateAsync(u);
        if (!res.Succeeded)
        {
			Error = string.Join("; ", res.Errors.Select(e => e.Description));
			Id = id;
			await OnGetAsync();
			return Page();
        }

        return RedirectToPage("/Setup/Users/Index");
    }
}
