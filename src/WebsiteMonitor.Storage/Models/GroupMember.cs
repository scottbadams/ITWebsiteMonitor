using WebsiteMonitor.Storage.Identity;

namespace WebsiteMonitor.Storage.Models;

public sealed class GroupMember
{
    public int GroupId { get; set; }
    public Group Group { get; set; } = default!;

    public string UserId { get; set; } = "";
    public ApplicationUser User { get; set; } = default!;
}
