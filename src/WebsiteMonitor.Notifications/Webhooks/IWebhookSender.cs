namespace WebsiteMonitor.Notifications.Webhooks;

public interface IWebhookSender
{
    Task PostAsync(string url, object payload, CancellationToken ct);
}
