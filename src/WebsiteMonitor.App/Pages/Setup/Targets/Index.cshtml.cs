using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.App.Pages.Setup.Targets;

public sealed class IndexModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;

    public IndexModel(WebsiteMonitorDbContext db)
    {
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public string InstanceId { get; set; } = "";

    public string? DisplayName { get; private set; }

    public sealed record Row(long TargetId, bool Enabled, string Url, string? LoginRule, int? HttpMin, int? HttpMax);
    public List<Row> Rows { get; private set; } = new();

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

        var targets = await _db.Targets
            .Where(t => t.InstanceId == InstanceId)
            .OrderBy(t => t.TargetId)
            .ToListAsync();

        Rows = targets.Select(t => new Row(t.TargetId, t.Enabled, t.Url, t.LoginRule, t.HttpExpectedStatusMin, t.HttpExpectedStatusMax)).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(long targetId)
    {
        var t = await _db.Targets.SingleOrDefaultAsync(x => x.TargetId == targetId && x.InstanceId == InstanceId);
        if (t == null) return NotFound();

        t.Enabled = !t.Enabled;
        await _db.SaveChangesAsync();

        return Redirect($"/setup/instances/{InstanceId}/targets{EmbedSuffix()}");
    }

    public async Task<IActionResult> OnPostDeleteAsync(long targetId)
    {
        var t = await _db.Targets.SingleOrDefaultAsync(x => x.TargetId == targetId && x.InstanceId == InstanceId);
        if (t == null) return NotFound();

        _db.Targets.Remove(t);
        await _db.SaveChangesAsync();

        return Redirect($"/setup/instances/{InstanceId}/targets{EmbedSuffix()}");
    }
}
