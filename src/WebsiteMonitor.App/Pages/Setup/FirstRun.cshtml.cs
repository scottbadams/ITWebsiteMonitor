using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Monitoring.Runtime;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.App.Pages.Setup;

public sealed class FirstRunModel : PageModel
{
    private static readonly Regex SlugRx = new("^[a-z0-9-]{1,64}$", RegexOptions.Compiled);

    private readonly WebsiteMonitorDbContext _db;
    private readonly IInstanceRuntimeManager _runtime;

    public FirstRunModel(WebsiteMonitorDbContext db, IInstanceRuntimeManager runtime)
    {
        _db = db;
        _runtime = runtime;
    }

    [BindProperty] public string InstanceId { get; set; } = "";
    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public string TimeZoneId { get; set; } = "America/Phoenix";
    [BindProperty] public int CheckIntervalSeconds { get; set; } = 60;
    [BindProperty] public bool Enabled { get; set; } = true;

    public string? Error { get; set; }

    public IActionResult OnGet()
        => Page();

    public async Task<IActionResult> OnPostAsync()
    {
        InstanceId = (InstanceId ?? "").Trim();
        DisplayName = (DisplayName ?? "").Trim();
        TimeZoneId = (TimeZoneId ?? "").Trim();

        if (string.IsNullOrWhiteSpace(InstanceId) || !SlugRx.IsMatch(InstanceId))
        {
            Error = "InstanceId must match: lowercase letters, numbers, hyphen (max 64).";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            Error = "DisplayName is required.";
            return Page();
        }

        var exists = await _db.Instances.AnyAsync(i => i.InstanceId == InstanceId);
        if (exists)
        {
            Error = "That InstanceId already exists.";
            return Page();
        }

        var instance = new Instance
        {
            InstanceId = InstanceId,
            DisplayName = DisplayName,
            Enabled = Enabled,
            TimeZoneId = string.IsNullOrWhiteSpace(TimeZoneId) ? "America/Phoenix" : TimeZoneId,
            CheckIntervalSeconds = Math.Max(5, CheckIntervalSeconds),
            ConcurrencyLimit = 20,
            WriteHtmlSnapshot = true,
            OutputFolder = null,
            CreatedUtc = DateTime.UtcNow
        };

        _db.Instances.Add(instance);
        await _db.SaveChangesAsync();

        if (instance.Enabled)
        {
            await _runtime.StartAsync(instance.InstanceId);
        }

        return Redirect($"/setup/instances/{instance.InstanceId}");
    }
}
