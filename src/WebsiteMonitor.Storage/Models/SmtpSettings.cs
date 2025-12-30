namespace WebsiteMonitor.Storage.Models;

public enum SmtpSecurityMode
{
    None = 0,
    SslTls = 1,   // Implicit TLS (SMTPS)
    StartTls = 2  // STARTTLS
}

public sealed class SmtpSettings
{
    // 1:1 per Instance
    public string InstanceId { get; set; } = default!;

    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public SmtpSecurityMode SecurityMode { get; set; } = SmtpSecurityMode.None;

    public string? Username { get; set; }

    // Protected/encrypted value (NOT plaintext)
    public string? PasswordProtected { get; set; }

    public string FromAddress { get; set; } = "";

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
