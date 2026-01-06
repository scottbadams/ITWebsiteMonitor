using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Identity;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.App.Pages.Setup.Groups;

[Authorize(Roles = "Admin")]
public sealed class EditModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<IdentityRole> _roles;

    public EditModel(WebsiteMonitorDbContext db, UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles)
    {
        _db = db;
        _users = users;
        _roles = roles;
    }

    [BindProperty(SupportsGet = true)] public int Id { get; set; }

    public string? Error { get; private set; }

    public GroupDto? Group { get; private set; }

    public List<string> AllRoles { get; private set; } = new();
    public List<(string Id, string Email)> AllUsers { get; private set; } = new();

    public HashSet<string> RoleNames { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UserIds { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed record GroupDto(int Id, string Name, bool Enabled);

    public async Task OnGetAsync()
    {
        try
        {
            var g = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Id);
            if (g is null) return;

            Group = new GroupDto(g.Id, g.Name, g.Enabled);

            AllRoles = await _roles.Roles.AsNoTracking().OrderBy(r => r.Name).Select(r => r.Name ?? "").Where(n => n != "").ToListAsync();
            AllUsers = await _users.Users.AsNoTracking().OrderBy(u => u.Email).Select(u => new ValueTuple<string,string>(u.Id, u.Email ?? "")).ToListAsync();

            RoleNames = (await _db.GroupRoles.AsNoTracking().Where(x => x.GroupId == Id).Select(x => x.RoleName).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            UserIds = (await _db.GroupMembers.AsNoTracking().Where(x => x.GroupId == Id).Select(x => x.UserId).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Error = ex.Message + " (Did you run the new EF migration?)";
        }
    }

    public async Task<IActionResult> OnPostAsync(int id, string name, bool enabled, string[] roleNames, string[] userIds)
    {
        try
        {
            var g = await _db.Groups.FirstOrDefaultAsync(x => x.Id == id);
            if (g is null) return RedirectToPage("/Setup/Groups/Index");

            g.Name = name.Trim();
            g.Enabled = enabled;

            // Replace roles
            var existingRoles = await _db.GroupRoles.Where(x => x.GroupId == id).ToListAsync();
            _db.GroupRoles.RemoveRange(existingRoles);
            foreach (var rn in roleNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(rn)) continue;
                _db.GroupRoles.Add(new GroupRole { GroupId = id, RoleName = rn.Trim() });
            }

            // Replace members
            var existingMembers = await _db.GroupMembers.Where(x => x.GroupId == id).ToListAsync();
            _db.GroupMembers.RemoveRange(existingMembers);
            foreach (var uid in userIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(uid)) continue;
                _db.GroupMembers.Add(new GroupMember { GroupId = id, UserId = uid });
            }

            await _db.SaveChangesAsync();
            return RedirectToPage("/Setup/Groups/Edit", new { id });
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            await OnGetAsync();
            return Page();
        }
    }
}
