using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Monitoring.Runtime;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.App.Pages.Setup.Instances;

public sealed class IndexModel : PageModel
{
    private readonly WebsiteMonitorDbContext _db;
    private readonly IInstanceRuntimeManager _runtime;

    public IndexModel(WebsiteMonitorDbContext db, IInstanceRuntimeManager runtime)
    {
        _db = db;
        _runtime = runtime;
    }

    [BindProperty(SupportsGet = true)]
    public string InstanceId { get; set; } = "";

    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public bool Enabled { get; set; } = true;
    [BindProperty] public string TimeZoneId { get; set; } = "America/Phoenix";
    [BindProperty] public int CheckIntervalSeconds { get; set; } = 60;
    [BindProperty] public int ConcurrencyLimit { get; set; } = 20;
    [BindProperty] public bool WriteHtmlSnapshot { get; set; } = true;
    [BindProperty] public string? OutputFolder { get; set; } = null;

    public string RuntimeState { get; private set; } = "Paused";
    public DateTime RuntimeChangedUtc { get; private set; } = DateTime.UtcNow;

    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var instance = await _db.Instances.SingleOrDefaultAsync(i => i.InstanceId == InstanceId);
        if (instance == null)
            return NotFound();

        DisplayName = instance.DisplayName;
        Enabled = instance.Enabled;
        TimeZoneId = instance.TimeZoneId;
        CheckIntervalSeconds = instance.CheckIntervalSeconds;
        ConcurrencyLimit = instance.ConcurrencyLimit;
        WriteHtmlSnapshot = instance.WriteHtmlSnapshot;
        OutputFolder = instance.OutputFolder;

        _runtime.TryGet(InstanceId, out var s);
        RuntimeState = s.State.ToString();
        RuntimeChangedUtc = s.ChangedUtc;

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
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

        return Redirect($"/setup/instances/{InstanceId}");
    }

    public async Task<IActionResult> OnPostStartAsync()
    {
        await _runtime.StartAsync(InstanceId);
        return Redirect($"/setup/instances/{InstanceId}");
    }

    public async Task<IActionResult> OnPostStopAsync()
    {
        await _runtime.StopAsync(InstanceId);
        return Redirect($"/setup/instances/{InstanceId}");
    }

    public async Task<IActionResult> OnPostRestartAsync()
    {
        await _runtime.RestartAsync(InstanceId);
        return Redirect($"/setup/instances/{InstanceId}");
    }
}
