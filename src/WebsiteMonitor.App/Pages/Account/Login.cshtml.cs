using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using WebsiteMonitor.Storage.Identity;

namespace WebsiteMonitor.App.Pages.Account;

public sealed class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users, ILogger<LoginModel> logger)
    {
        _signIn = signIn;
        _users = users;
        _logger = logger;
    }

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; } = true;

    public string? Error { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var identifier = (Email ?? "").Trim();

        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrEmpty(Password))
        {
            Error = "Login failed.";
            return Page();
        }

        // Support logging in with either username or email.
        var user = await _users.FindByNameAsync(identifier) ?? await _users.FindByEmailAsync(identifier);
        if (user == null)
        {
            _logger.LogWarning("Login failed: user not found. Host={Host} RemoteIP={RemoteIP} Identifier={Identifier}",
                Request.Host.Value,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                identifier);

            Error = "Login failed.";
            return Page();
        }

        var pwOk = await _users.CheckPasswordAsync(user, Password);

        _logger.LogInformation("Login attempt. Host={Host} RemoteIP={RemoteIP} Identifier={Identifier} UserName={UserName} Email={Email} PasswordOk={PasswordOk}",
            Request.Host.Value,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            identifier,
            user.UserName,
            user.Email,
            pwOk);

        var result = await _signIn.PasswordSignInAsync(user.UserName!, Password, RememberMe, lockoutOnFailure: false);

        _logger.LogInformation("Login result. Succeeded={Succeeded} IsLockedOut={IsLockedOut} IsNotAllowed={IsNotAllowed} RequiresTwoFactor={RequiresTwoFactor} Host={Host} RemoteIP={RemoteIP} UserName={UserName}",
            result.Succeeded,
            result.IsLockedOut,
            result.IsNotAllowed,
            result.RequiresTwoFactor,
            Request.Host.Value,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            user.UserName);

        if (!result.Succeeded)
        {
            Error = "Login failed.";
            return Page();
        }

        return Redirect("/setup");
    }
}
