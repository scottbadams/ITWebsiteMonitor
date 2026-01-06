using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Identity;

namespace WebsiteMonitor.App.Infrastructure;

public sealed class GroupRoleClaimsTransformation : IClaimsTransformation
{
    private const string MarkerType = "wm:claims_loaded";
    private const string ThemeType  = "wm:theme";

    private readonly WebsiteMonitorDbContext _db;
    private readonly ILogger<GroupRoleClaimsTransformation> _log;

    public GroupRoleClaimsTransformation(WebsiteMonitorDbContext db, ILogger<GroupRoleClaimsTransformation> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return principal;

        // Prevent running multiple times within the same request.
        if (principal.HasClaim(c => c.Type == MarkerType)) return principal;

        var id = principal.Identities.FirstOrDefault(i => i.IsAuthenticated);
        if (id is null) return principal;

        id.AddClaim(new Claim(MarkerType, "1"));

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return principal;

        // Theme preference
        try
        {
            var user = await _db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.DarkThemeEnabled })
                .FirstOrDefaultAsync();

            if (user is not null)
            {
                id.AddClaim(new Claim(ThemeType, user.DarkThemeEnabled ? "dark" : "light"));
            }
        }
        catch (Exception ex)
        {
            // If migrations haven't been applied yet or identity tables aren't available, don't fail the request.
            _log.LogDebug(ex, "Theme claim transform skipped.");
        }

        // Group -> Role projection
        try
        {
            var roleNames = await (
                from gm in _db.GroupMembers.AsNoTracking()
                join g in _db.Groups.AsNoTracking() on gm.GroupId equals g.Id
                join gr in _db.GroupRoles.AsNoTracking() on g.Id equals gr.GroupId
                where gm.UserId == userId && g.Enabled
                select gr.RoleName
            ).Distinct().ToListAsync();

            foreach (var rn in roleNames)
            {
                if (string.IsNullOrWhiteSpace(rn)) continue;
                if (!principal.IsInRole(rn))
                {
                    id.AddClaim(new Claim(ClaimTypes.Role, rn));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Group role claim transform skipped.");
        }

        return principal;
    }
}
