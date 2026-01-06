using Microsoft.AspNetCore.Identity;

namespace WebsiteMonitor.Storage.Identity;

public sealed class ApplicationUser : IdentityUser
{
    /// <summary>
    /// User's full name (optional).
    /// </summary>
    public string? FullName { get; set; }

    /// <summary>
    /// If true, force dark theme for this user; if false, force light theme.
    /// </summary>
    public bool DarkThemeEnabled { get; set; } = false;
}
