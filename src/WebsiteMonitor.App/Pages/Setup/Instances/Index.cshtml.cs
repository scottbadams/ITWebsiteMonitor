using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.App.Infrastructure;
using WebsiteMonitor.Monitoring.Runtime;
using WebsiteMonitor.Notifications;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.App.Pages.Setup.Instances;

public sealed class IndexModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;
    private readonly IInstanceRuntimeManager _runtime;
    private readonly SmtpPasswordProtector _protector;
    private readonly ISmtpEmailSender _smtp;

    public IndexModel(
        WebsiteMonitorDbContext db,
        IInstanceRuntimeManager runtime,
        SmtpPasswordProtector protector,
        ISmtpEmailSender smtp)
    {
        _db = db;
        _runtime = runtime;
        _protector = protector;
        _smtp = smtp;
    }

    [BindProperty(SupportsGet = true)]
    public string InstanceId { get; set; } = "";

    // Instance settings
    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public bool Enabled { get; set; } = true;
    [BindProperty] public string TimeZoneId { get; set; } = "America/Phoenix";
    [BindProperty] public int CheckIntervalSeconds { get; set; } = 60;
    [BindProperty] public int ConcurrencyLimit { get; set; } = 20;
    [BindProperty] public bool WriteHtmlSnapshot { get; set; } = true;
    [BindProperty] public string? OutputFolder { get; set; } = null;

    // SMTP settings
    [BindProperty] public string SmtpHost { get; set; } = "";
    [BindProperty] public int SmtpPort { get; set; } = 25;
    [BindProperty] public string SmtpSecurityMode { get; set; } = "None"; // None | SslTls | StartTls
    [BindProperty] public string? SmtpUsername { get; set; } = null;

    // IMPORTANT: blank password means "do not change"
    [BindProperty] public string? SmtpPassword { get; set; } = null;

    [BindProperty] public string SmtpFromAddress { get; set; } = "";

    // Recipients: one email per line
    [BindProperty] public string RecipientsText { get; set; } = "";

    public string RuntimeState { get; private set; } = "Paused";
    public DateTime RuntimeChangedUtc { get; private set; } = DateTime.UtcNow;

    public string? Error { get; set; }
    public string? Info { get; set; }


    private static bool IsTruthy(string? value)
    {
        var v = (value ?? "").Trim();
        return string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private string EmbedSuffix()
    {
        var q = Request?.Query;
        if (q == null || q.Count == 0) return "";

        var parts = new System.Collections.Generic.List<string>();

        var embed = q["embed"].ToString();
        if (IsTruthy(embed)) parts.Add("embed=1");

        var z = q["z"].ToString();
        if (IsTruthy(z)) parts.Add("z=1");

        var scheme = (q["scheme"].ToString() ?? "").Trim();
        if (string.Equals(scheme, "light", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(scheme, "dark", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("scheme=" + scheme.ToLowerInvariant());
        }

        return parts.Count == 0 ? "" : ("?" + string.Join("&", parts));
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(InstanceId))
            return NotFound();

        var instance = await _db.Instances.SingleOrDefaultAsync(i => i.InstanceId == InstanceId);
        if (instance == null)
            return NotFound();

        // Instance fields
        DisplayName = instance.DisplayName;
        Enabled = instance.Enabled;
        TimeZoneId = instance.TimeZoneId;
        CheckIntervalSeconds = instance.CheckIntervalSeconds;
        ConcurrencyLimit = instance.ConcurrencyLimit;
        WriteHtmlSnapshot = instance.WriteHtmlSnapshot;
        OutputFolder = instance.OutputFolder;

        // Runtime status
        _runtime.TryGet(InstanceId, out var s);
        RuntimeState = s.State.ToString();
        RuntimeChangedUtc = s.ChangedUtc;

        // SMTP fields
        var smtp = await _db.SmtpSettings.SingleOrDefaultAsync(x => x.InstanceId == InstanceId);
        if (smtp != null)
        {
            SmtpHost = smtp.Host ?? "";
            SmtpPort = smtp.Port;
            SmtpSecurityMode = smtp.SecurityMode.ToString();
            SmtpUsername = smtp.Username;
            SmtpFromAddress = smtp.FromAddress ?? "";
        }

        // Recipients
        var recips = await _db.Recipients
            .Where(r => r.InstanceId == InstanceId && r.Enabled)
            .OrderBy(r => r.Email)
            .Select(r => r.Email)
            .ToListAsync();

        RecipientsText = string.Join(Environment.NewLine, recips);

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        // Save instance settings only
        var instance = await _db.Instances.SingleOrDefaultAsync(i => i.InstanceId == InstanceId);
        if (instance == null)
            return NotFound();

        DisplayName = (DisplayName ?? "").Trim();
        TimeZoneId = (TimeZoneId ?? "").Trim();
        OutputFolder = string.IsNullOrWhiteSpace(OutputFolder) ? null : OutputFolder.Trim();

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            Error = "DisplayName is required.";
            return await OnGetAsync();
        }

        instance.DisplayName = DisplayName;
        instance.Enabled = Enabled;
        instance.TimeZoneId = string.IsNullOrWhiteSpace(TimeZoneId) ? "America/Phoenix" : TimeZoneId;
        instance.CheckIntervalSeconds = Math.Max(5, CheckIntervalSeconds);
        instance.ConcurrencyLimit = Math.Max(1, ConcurrencyLimit);
        instance.WriteHtmlSnapshot = WriteHtmlSnapshot;
        instance.OutputFolder = OutputFolder;

        await _db.SaveChangesAsync();

        // If disabled, stop runtime now.
        if (!instance.Enabled)
            await _runtime.StopAsync(instance.InstanceId);

        return Redirect($"/setup/instances/{InstanceId}{EmbedSuffix()}");
    }

    // Saves SMTP settings + recipients. Returns back to same page.
    public async Task<IActionResult> OnPostSaveSmtpAsync()
    {
        var instance = await _db.Instances.SingleOrDefaultAsync(i => i.InstanceId == InstanceId);
        if (instance == null)
            return NotFound();

        // Validate basics
        SmtpHost = (SmtpHost ?? "").Trim();
        SmtpUsername = string.IsNullOrWhiteSpace(SmtpUsername) ? null : SmtpUsername.Trim();
        SmtpFromAddress = (SmtpFromAddress ?? "").Trim();
        SmtpSecurityMode = (SmtpSecurityMode ?? "None").Trim();

        if (string.IsNullOrWhiteSpace(SmtpHost))
        {
            Error = "SMTP Host is required to save SMTP settings.";
            return await OnGetAsync();
        }

        if (SmtpPort <= 0 || SmtpPort > 65535)
        {
            Error = "SMTP Port must be between 1 and 65535.";
            return await OnGetAsync();
        }

        if (string.IsNullOrWhiteSpace(SmtpFromAddress) || !SmtpFromAddress.Contains("@"))
        {
            Error = "From Address must be a valid email address.";
            return await OnGetAsync();
        }

        if (SmtpSecurityMode is not ("None" or "SslTls" or "StartTls"))
        {
            Error = "Security Mode must be None, SslTls, or StartTls.";
            return await OnGetAsync();
        }

        var smtp = await _db.SmtpSettings.SingleOrDefaultAsync(x => x.InstanceId == InstanceId);
        if (smtp == null)
        {
            smtp = new SmtpSettings { InstanceId = InstanceId };
            _db.SmtpSettings.Add(smtp);
        }

        smtp.Host = SmtpHost;
        smtp.Port = SmtpPort;
        smtp.SecurityMode = Enum.TryParse<WebsiteMonitor.Storage.Models.SmtpSecurityMode>(SmtpSecurityMode, true, out var mode) ? mode : WebsiteMonitor.Storage.Models.SmtpSecurityMode.None;
        smtp.Username = SmtpUsername;
        smtp.FromAddress = SmtpFromAddress;
        smtp.UpdatedUtc = DateTime.UtcNow;

        // Only update stored password if the user typed one
        if (!string.IsNullOrWhiteSpace(SmtpPassword))
        {
            smtp.PasswordProtected = _protector.Protect(SmtpPassword);
        }

        // Save recipients: replace enabled list with parsed list
        var lines = (RecipientsText ?? "")
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Disable all existing recipients for this instance, then upsert the ones provided
        var existing = await _db.Recipients.Where(r => r.InstanceId == InstanceId).ToListAsync();
        foreach (var r in existing)
            r.Enabled = false;

        foreach (var email in lines)
        {
            if (!email.Contains("@"))
                continue;

            var r = existing.FirstOrDefault(x => x.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
            if (r == null)
            {
                _db.Recipients.Add(new Recipient
                {
                    InstanceId = InstanceId,
                    Email = email,
                    Enabled = true,
                    CreatedUtc = DateTime.UtcNow
                });
            }
            else
            {
                r.Enabled = true;
            }
        }

        await _db.SaveChangesAsync();

        Info = "SMTP settings and recipients saved.";
        TempData["Flash"] = "SMTP settings and recipients saved.";
		return Redirect($"/setup/instances/{InstanceId}{EmbedSuffix()}");
    }

    // Saves SMTP settings, then opens the SMTP Test popup in a new window/tab.
	public async Task<IActionResult> OnPostSaveAndTestAsync()
	{
		// Save SMTP + recipients first
		var saveResult = await OnPostSaveSmtpAsync();
		if (saveResult is PageResult)
			return saveResult; // validation errors shown on the instance page

		// Then open the test page (new tab because formtarget=_blank)
		return Redirect($"/setup/instances/{InstanceId}/smtptest");
	}

    public async Task<IActionResult> OnPostStartAsync()
    {
        await _runtime.StartAsync(InstanceId);
        return Redirect($"/setup/instances/{InstanceId}{EmbedSuffix()}");
    }

    public async Task<IActionResult> OnPostStopAsync()
    {
        await _runtime.StopAsync(InstanceId);
        return Redirect($"/setup/instances/{InstanceId}{EmbedSuffix()}");
    }

    public async Task<IActionResult> OnPostRestartAsync()
    {
        await _runtime.RestartAsync(InstanceId);
        return Redirect($"/setup/instances/{InstanceId}{EmbedSuffix()}");
    }
}
