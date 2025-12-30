using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using WebsiteMonitor.App.Infrastructure;
using WebsiteMonitor.Notifications.Smtp;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.App.Pages.Setup.Instances;

public sealed class SmtpTestModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;
    private readonly ISmtpSender _sender;
    private readonly IDataProtector _protector;

    public SmtpTestModel(WebsiteMonitorDbContext db, ISmtpSender sender, IDataProtectionProvider dp)
    {
        _db = db;
        _sender = sender;
        _protector = dp.CreateProtector(SecurityPurposes.SmtpPassword);
    }

    [BindProperty(SupportsGet = true)]
    public string InstanceId { get; set; } = "";

    [BindProperty]
    public string FromAddress { get; set; } = "";

    [BindProperty]
    public string ToAddress { get; set; } = "";

    public string? Error { get; set; }
    public string? Info { get; set; }

	[BindProperty]
	public string? NewPassword { get; set; }


    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(InstanceId))
            return NotFound();

        var instance = await _db.Instances.SingleOrDefaultAsync(i => i.InstanceId == InstanceId);
        if (instance == null)
            return NotFound();

        var smtp = await _db.SmtpSettings.SingleOrDefaultAsync(s => s.InstanceId == InstanceId);
        if (smtp == null)
        {
            Error = "SMTP settings are not configured for this instance yet. Go back and save SMTP settings first.";
            return Page();
        }

        FromAddress = smtp.FromAddress ?? "";

        // Default To: first recipient if present
        var first = await _db.Recipients
            .Where(r => r.InstanceId == InstanceId)
            .OrderBy(r => r.Email)
            .Select(r => r.Email)
            .FirstOrDefaultAsync();

        ToAddress = first ?? "";

        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync()
    {
        if (string.IsNullOrWhiteSpace(InstanceId))
            return NotFound();

        var smtp = await _db.SmtpSettings.SingleOrDefaultAsync(s => s.InstanceId == InstanceId);
        if (smtp == null)
        {
            Error = "SMTP settings not found. Save SMTP settings first.";
            return Page();
        }

        FromAddress = smtp.FromAddress ?? FromAddress;

        if (string.IsNullOrWhiteSpace(ToAddress))
        {
            Error = "Recipient (To) is required.";
            return Page();
        }

		if (!string.IsNullOrWhiteSpace(NewPassword))
		{
			smtp.PasswordProtected = _protector.Protect(NewPassword);
			smtp.UpdatedUtc = DateTime.UtcNow;
			await _db.SaveChangesAsync(HttpContext.RequestAborted);
		}

        string? pw = null;
        if (!string.IsNullOrWhiteSpace(smtp.PasswordProtected))
        {
            try { pw = _protector.Unprotect(smtp.PasswordProtected); }
            catch
            {
                Error = "Stored SMTP password could not be decrypted. Re-enter password on the Instance page and Save.";
                return Page();
            }
        }

        try
        {
            await _sender.SendAsync(
                smtp,
                pw,
                ToAddress.Trim(),
                subject: $"WebsiteMonitor SMTP Test ({InstanceId})",
                bodyText: $"Test message sent at {DateTime.UtcNow:u} for instance {InstanceId}.",
                ct: HttpContext.RequestAborted);

            Info = "SMTP test message sent successfully.";
        }
        catch (Exception ex)
        {
            Error = "SMTP send failed: " + ex.Message;
        }

        return Page();
    }
}
