namespace WebsiteMonitor.Notifications;

public interface ISmtpEmailSender
{
    Task SendAsync(
        string host,
        int port,
        string securityMode,   // "None" | "SslTls" | "StartTls"
        string? username,
        string? password,
        string fromAddress,
        string toAddress,
        string subject,
        string bodyText,
        CancellationToken ct);
}
