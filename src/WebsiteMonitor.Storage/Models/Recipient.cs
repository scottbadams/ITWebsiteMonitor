namespace WebsiteMonitor.Storage.Models;

public sealed class Recipient
{
    public long RecipientId { get; set; }

    public string InstanceId { get; set; } = default!;

    public string Email { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
