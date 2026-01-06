namespace WebsiteMonitor.Storage.Models;

public sealed class Group
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<GroupMember> Members { get; set; } = new List<GroupMember>();

    public ICollection<GroupRole> Roles { get; set; } = new List<GroupRole>();
}
