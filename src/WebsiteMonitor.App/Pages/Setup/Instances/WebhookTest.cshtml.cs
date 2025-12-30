using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Notifications.Webhooks;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.App.Pages.Setup.Instances;

[Authorize(Roles = "Admin")]
public sealed class WebhookTestModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;
    private readonly IWebhookSender _sender;

    public WebhookTestModel(WebsiteMonitorDbContext db, IWebhookSender sender)
    {
        _db = db;
        _sender = sender;
    }

    [BindProperty(SupportsGet = true)]
    public string InstanceId { get; set; } = "";

    public List<string> EnabledUrls { get; private set; } = new();

    public string? Error { get; set; }
    public string? Info { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(InstanceId))
            return NotFound();

        EnabledUrls = await _db.WebhookEndpoints
            .Where(w => w.InstanceId == InstanceId && w.Enabled)
            .Select(w => w.Url)
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync()
    {
        if (string.IsNullOrWhiteSpace(InstanceId))
            return NotFound();

        EnabledUrls = await _db.WebhookEndpoints
            .Where(w => w.InstanceId == InstanceId && w.Enabled)
            .Select(w => w.Url)
            .ToListAsync();

        if (EnabledUrls.Count == 0)
        {
            Error = "No enabled webhooks for this instance.";
            return Page();
        }

        var payload = new
        {
            eventType = "Test",
            instanceId = InstanceId,
            timestampUtc = DateTime.UtcNow,
            message = "Webhook test from Setup UI"
        };

        try
        {
            foreach (var url in EnabledUrls)
                await _sender.PostAsync(url, payload, HttpContext.RequestAborted);

            Info = "Webhook test sent successfully to all enabled endpoints.";
        }
        catch (Exception ex)
        {
            Error = "Webhook test failed: " + ex.Message;
        }

        return Page();
    }
}
