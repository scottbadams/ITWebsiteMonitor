using Microsoft.AspNetCore.DataProtection;

namespace WebsiteMonitor.App.Infrastructure;

public sealed class SmtpPasswordProtector
{
    private readonly IDataProtector _protector;

    public SmtpPasswordProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(SecurityPurposes.SmtpPassword);
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}
