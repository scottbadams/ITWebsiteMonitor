namespace WebsiteMonitor.Storage.Models;

public sealed class GroupRole
{
    public int GroupId { get; set; }
    public Group Group { get; set; } = default!;

    /// <summary>
    /// Identity role name to grant to members of this group (e.g. "Admin").
    /// </summary>
    public string RoleName { get; set; } = "";
}
