using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebsiteMonitor.Storage.Identity;

namespace WebsiteMonitor.App.Pages.Bootstrap;

public sealed class IndexModel : PageModel
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;

    public IndexModel(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn)
    {
        _users = users;
        _signIn = signIn;
    }

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; set; }

    public IActionResult OnGet()
    {
        // If any user exists, bootstrap is already done. Go to login.
        if (_users.Users.Any())
        return Redirect("/account/login");

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (_users.Users.Any())
            return Redirect("/");

        var user = new ApplicationUser { UserName = Email, Email = Email };
        var result = await _users.CreateAsync(user, Password);
        if (!result.Succeeded)
        {
            Error = string.Join("; ", result.Errors.Select(e => e.Description));
            return Page();
        }

        await _users.AddToRoleAsync(user, "Admin"); // role will be created in Step 3.6
        await _signIn.SignInAsync(user, isPersistent: true);
        return Redirect("/setup");
    }
}
