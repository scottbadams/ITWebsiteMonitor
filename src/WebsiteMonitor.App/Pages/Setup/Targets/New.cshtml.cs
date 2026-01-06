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

    public string? DisplayName { get; private set; }

    [BindProperty] public string TargetUrl { get; set; } = "";
    [BindProperty] public bool Enabled { get; set; } = true;
    [BindProperty] public int HttpMin { get; set; } = 200;
    [BindProperty] public int HttpMax { get; set; } = 399;

    public string? Error { get; set; }

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
        var inst = await _db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.InstanceId == InstanceId);
        if (inst == null) return NotFound();
        DisplayName = inst.DisplayName;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var inst = await _db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.InstanceId == InstanceId);
        if (inst == null) return NotFound();
        DisplayName = inst.DisplayName;

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

        return Redirect($"/setup/instances/{InstanceId}/targets{EmbedSuffix()}");
    }
}
