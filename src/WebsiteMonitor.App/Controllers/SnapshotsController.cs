using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.App.Infrastructure;
using WebsiteMonitor.Storage.Data;

namespace WebsiteMonitor.App.Controllers;

[ApiController]
public sealed class SnapshotsController : ControllerBase
{
    private readonly WebsiteMonitorDbContext _db;
    private readonly ProductPaths _paths;

    public SnapshotsController(WebsiteMonitorDbContext db, ProductPaths paths)
    {
        _db = db;
        _paths = paths;
    }

    // GET /snapshots/{instanceId}/{targetId}
    [HttpGet("/snapshots/{instanceId}/{targetId:long}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetTargetSnapshot(string instanceId, long targetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return NotFound();

        var inst = await _db.Instances
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.InstanceId == instanceId, ct);

        if (inst == null || string.IsNullOrWhiteSpace(inst.OutputFolder))
            return NotFound();

        var outputFolder = Path.IsPathRooted(inst.OutputFolder!)
            ? inst.OutputFolder!
            : Path.Combine(_paths.DataRoot, inst.OutputFolder!);

        var rootFull = Path.GetFullPath(outputFolder);
        var fileFull = Path.GetFullPath(Path.Combine(outputFolder, "targets", $"{targetId}.html"));

        // basic containment check
        if (!fileFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (!System.IO.File.Exists(fileFull))
            return NotFound();

        return PhysicalFile(fileFull, "text/html; charset=utf-8");
    }
}
