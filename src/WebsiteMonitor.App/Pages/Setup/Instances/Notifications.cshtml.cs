using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.App.Pages.Setup.Instances;

[Authorize(Roles = "Admin")]
public sealed class NotificationsModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;
    public NotificationsModel(WebsiteMonitorDbContext db) => _db = db;

    [BindProperty(SupportsGet = true)]
    public string InstanceId { get; set; } = "";

    [BindProperty]
    public string NewRecipientEmail { get; set; } = "";

    [BindProperty]
    public string NewWebhookUrl { get; set; } = "";

    public List<Recipient> Recipients { get; private set; } = new();
    public List<WebhookEndpoint> Webhooks { get; private set; } = new();

    [TempData] public string? Info { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(InstanceId))
            return NotFound();

        var instanceExists = await _db.Instances.AnyAsync(i => i.InstanceId == InstanceId);
        if (!instanceExists)
            return NotFound();

        Recipients = await _db.Recipients
            .Where(r => r.InstanceId == InstanceId)
            .OrderBy(r => r.Email)
            .ToListAsync();

        Webhooks = await _db.WebhookEndpoints
            .Where(w => w.InstanceId == InstanceId)
            .OrderBy(w => w.Url)
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostAddRecipientAsync()
    {
        if (string.IsNullOrWhiteSpace(InstanceId))
            return NotFound();

        var email = (NewRecipientEmail ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            Error = "Recipient email is required.";
            return RedirectToPage(new { InstanceId });
        }

        _db.Recipients.Add(new Recipient { InstanceId = InstanceId, Email = email, Enabled = true });

        try
        {
            await _db.SaveChangesAsync();
            Info = "Recipient added.";
        }
        catch (DbUpdateException)
        {
            Error = "Recipient already exists (or invalid).";
        }

        return RedirectToPage(new { InstanceId });
    }

    public async Task<IActionResult> OnPostToggleRecipientAsync(long id)
    {
        var r = await _db.Recipients.SingleOrDefaultAsync(x => x.RecipientId == id && x.InstanceId == InstanceId);
        if (r == null) return RedirectToPage(new { InstanceId });

        r.Enabled = !r.Enabled;
        await _db.SaveChangesAsync();
        return RedirectToPage(new { InstanceId });
    }

    public async Task<IActionResult> OnPostDeleteRecipientAsync(long id)
    {
        var r = await _db.Recipients.SingleOrDefaultAsync(x => x.RecipientId == id && x.InstanceId == InstanceId);
        if (r == null) return RedirectToPage(new { InstanceId });

        _db.Recipients.Remove(r);
        await _db.SaveChangesAsync();
        return RedirectToPage(new { InstanceId });
    }

    public async Task<IActionResult> OnPostAddWebhookAsync()
    {
        if (string.IsNullOrWhiteSpace(InstanceId))
            return NotFound();

        var url = (NewWebhookUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            Error = "Webhook URL is required.";
            return RedirectToPage(new { InstanceId });
        }

        _db.WebhookEndpoints.Add(new WebhookEndpoint
        {
            InstanceId = InstanceId,
            Url = url,
            Enabled = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });

        try
        {
            await _db.SaveChangesAsync();
            Info = "Webhook added.";
        }
        catch (DbUpdateException)
        {
            Error = "Webhook already exists (or invalid).";
        }

        return RedirectToPage(new { InstanceId });
    }

    public async Task<IActionResult> OnPostToggleWebhookAsync(long id)
    {
        var w = await _db.WebhookEndpoints.SingleOrDefaultAsync(x => x.WebhookEndpointId == id && x.InstanceId == InstanceId);
        if (w == null) return RedirectToPage(new { InstanceId });

        w.Enabled = !w.Enabled;
        w.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return RedirectToPage(new { InstanceId });
    }

    public async Task<IActionResult> OnPostDeleteWebhookAsync(long id)
    {
        var w = await _db.WebhookEndpoints.SingleOrDefaultAsync(x => x.WebhookEndpointId == id && x.InstanceId == InstanceId);
        if (w == null) return RedirectToPage(new { InstanceId });

        _db.WebhookEndpoints.Remove(w);
        await _db.SaveChangesAsync();
        return RedirectToPage(new { InstanceId });
    }
}
