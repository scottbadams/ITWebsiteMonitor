using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace WebsiteMonitor.Notifications;

public sealed class MailKitSmtpEmailSender : ISmtpEmailSender
{
    public async Task SendAsync(
        string host,
        int port,
        string securityMode,
        string? username,
        string? password,
        string fromAddress,
        string toAddress,
        string subject,
        string bodyText,
        CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(fromAddress));
        msg.To.Add(MailboxAddress.Parse(toAddress));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = bodyText };

        var options = securityMode switch
        {
            "SslTls" => SecureSocketOptions.SslOnConnect,
            "StartTls" => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None
        };

        using var client = new SmtpClient();
        await client.ConnectAsync(host, port, options, ct);

        if (!string.IsNullOrWhiteSpace(username))
            await client.AuthenticateAsync(username, password ?? "", ct);

        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }
}
