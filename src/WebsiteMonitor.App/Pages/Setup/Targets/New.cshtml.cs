using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.App.Pages.Setup.Targets;

public sealed class NewModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;

    public NewModel(WebsiteMonitorDbContext db)
    {
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public string InstanceId { get; set; } = "";

    [BindProperty] public string TargetUrl { get; set; } = "";
    [BindProperty] public bool Enabled { get; set; } = true;
    [BindProperty] public int HttpMin { get; set; } = 200;
    [BindProperty] public int HttpMax { get; set; } = 399;

    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var exists = await _db.Instances.AnyAsync(i => i.InstanceId == InstanceId);
        if (!exists) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var exists = await _db.Instances.AnyAsync(i => i.InstanceId == InstanceId);
        if (!exists) return NotFound();

        TargetUrl = (TargetUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(TargetUrl))
        {
            Error = "URL is required.";
            return Page();
        }

        // Minimal sanity check (real URL validation later)
        if (!Uri.TryCreate(TargetUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            Error = "URL must be an absolute http/https URL.";
            return Page();
        }

        var target = new Target
        {
            InstanceId = InstanceId,
            Url = TargetUrl,
            Enabled = Enabled,
            HttpExpectedStatusMin = HttpMin,
            HttpExpectedStatusMax = HttpMax,
            CreatedUtc = DateTime.UtcNow
        };

        _db.Targets.Add(target);
        await _db.SaveChangesAsync();

        return Redirect($"/setup/instances/{InstanceId}/targets");
    }
}
