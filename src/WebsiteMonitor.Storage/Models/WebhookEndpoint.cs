namespace WebsiteMonitor.Storage.Models;

public sealed class WebhookEndpoint
{
    public long WebhookEndpointId { get; set; }

    public string InstanceId { get; set; } = default!;

    public string Url { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
