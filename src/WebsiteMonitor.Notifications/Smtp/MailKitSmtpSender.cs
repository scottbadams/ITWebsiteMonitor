using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.Notifications.Smtp;

public sealed class MailKitSmtpSender : ISmtpSender
{
    public async Task SendAsync(
        SmtpSettings settings,
        string? passwordPlain,
        string toEmail,
        string subject,
        string bodyText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
            throw new InvalidOperationException("SMTP host is not set.");

        if (settings.Port <= 0)
            throw new InvalidOperationException("SMTP port is not set.");

        if (string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new InvalidOperationException("From address is not set.");

        if (string.IsNullOrWhiteSpace(toEmail))
            throw new InvalidOperationException("Recipient address is not set.");

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(settings.FromAddress));
        msg.To.Add(MailboxAddress.Parse(toEmail));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = bodyText };

        var opt = settings.SecurityMode switch
        {
            SmtpSecurityMode.None => SecureSocketOptions.None,
            SmtpSecurityMode.SslTls => SecureSocketOptions.SslOnConnect,   // implicit TLS (e.g., 465)
            SmtpSecurityMode.StartTls => SecureSocketOptions.StartTls,     // STARTTLS (e.g., 587)
            _ => SecureSocketOptions.Auto
        };

        using var client = new SmtpClient();

        await client.ConnectAsync(settings.Host, settings.Port, opt, ct);

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            // passwordPlain can be empty if user never stored a password yet
            await client.AuthenticateAsync(settings.Username, passwordPlain ?? "", ct);
        }

        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }
}
