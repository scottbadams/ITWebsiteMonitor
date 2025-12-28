using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using WebsiteMonitor.Storage.Identity;

namespace WebsiteMonitor.App.Pages.Account;

public sealed class LogoutModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signIn;

    public LogoutModel(SignInManager<ApplicationUser> signIn)
    {
        _signIn = signIn;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await _signIn.SignOutAsync();
        return Redirect("/");
    }
}
