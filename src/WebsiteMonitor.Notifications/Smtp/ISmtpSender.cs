using System.Threading;
using System.Threading.Tasks;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.Notifications.Smtp;

public interface ISmtpSender
{
    Task SendAsync(
        SmtpSettings settings,
        string? passwordPlain,
        string toEmail,
        string subject,
        string bodyText,
        CancellationToken ct = default);
}
