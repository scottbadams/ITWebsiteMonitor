using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Models;
using StorageSystemSettings = WebsiteMonitor.Storage.Models.SystemSettings;

namespace WebsiteMonitor.App.Pages.Setup.SystemSettings;

[Authorize(Roles = "Admin")]
public sealed class IndexModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;

    public IndexModel(WebsiteMonitorDbContext db)
    {
        _db = db;
    }

    public sealed record TimeZoneRow(string Id, string DisplayName);

    public sealed class InputModel
    {
        public string? LogoPath { get; set; }
        public string? DefaultTimeZoneId { get; set; }
        public int? DefaultCheckIntervalSeconds { get; set; }
        public int? DefaultConcurrencyLimit { get; set; }
        public string? DefaultSnapshotOutputFolderTemplate { get; set; }
        public bool AllowNetworkAccess { get; set; }
        public string? PublicBaseUrl { get; set; }
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public string? Error { get; private set; }

    public List<TimeZoneRow> TimeZones { get; private set; } = new();

    public async Task OnGetAsync()
    {
        TimeZones = TimeZoneInfo.GetSystemTimeZones()
            .Select(tz => new TimeZoneRow(tz.Id, tz.DisplayName))
            .OrderBy(t => t.DisplayName)
            .ToList();

        try
        {
            var ss = await _db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1);
            if (ss is not null)
            {
                Input.LogoPath = ss.LogoPath;
                Input.DefaultTimeZoneId = ss.DefaultTimeZoneId;
                Input.DefaultCheckIntervalSeconds = ss.DefaultCheckIntervalSeconds;
                Input.DefaultConcurrencyLimit = ss.DefaultConcurrencyLimit;
                Input.DefaultSnapshotOutputFolderTemplate = ss.DefaultSnapshotOutputFolderTemplate;
                Input.AllowNetworkAccess = ss.AllowNetworkAccess;
                Input.PublicBaseUrl = ss.PublicBaseUrl;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message + " (Did you run the new EF migration?)";
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        TimeZones = TimeZoneInfo.GetSystemTimeZones()
            .Select(tz => new TimeZoneRow(tz.Id, tz.DisplayName))
            .OrderBy(t => t.DisplayName)
            .ToList();

        try
        {
            var ss = await _db.SystemSettings.FirstOrDefaultAsync(x => x.Id == 1);
            if (ss is null)
            {
                ss = new StorageSystemSettings { Id = 1 };
                _db.SystemSettings.Add(ss);
            }

            ss.LogoPath = string.IsNullOrWhiteSpace(Input.LogoPath) ? null : Input.LogoPath.Trim();
            ss.DefaultTimeZoneId = string.IsNullOrWhiteSpace(Input.DefaultTimeZoneId) ? null : Input.DefaultTimeZoneId.Trim();
            ss.DefaultCheckIntervalSeconds = Input.DefaultCheckIntervalSeconds;
            ss.DefaultConcurrencyLimit = Input.DefaultConcurrencyLimit;
            ss.DefaultSnapshotOutputFolderTemplate = string.IsNullOrWhiteSpace(Input.DefaultSnapshotOutputFolderTemplate) ? null : Input.DefaultSnapshotOutputFolderTemplate.Trim();

            
            ss.AllowNetworkAccess = Input.AllowNetworkAccess;
            ss.PublicBaseUrl = string.IsNullOrWhiteSpace(Input.PublicBaseUrl) ? null : Input.PublicBaseUrl.Trim();
await _db.SaveChangesAsync();
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return Page();
        }
    }
}
