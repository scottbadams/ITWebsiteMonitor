using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using WebsiteMonitor.Storage.Identity;

namespace WebsiteMonitor.App.Pages.Account;

public sealed class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signIn;

    public LoginModel(SignInManager<ApplicationUser> signIn)
    {
        _signIn = signIn;
    }

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; } = true;

    public string? Error { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var result = await _signIn.PasswordSignInAsync(Email, Password, RememberMe, lockoutOnFailure: false);
        if (!result.Succeeded)
        {
            Error = "Login failed.";
            return Page();
        }

        return Redirect("/setup");
    }
}
