using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Monitoring.Runtime;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.App.Controllers;

[ApiController]
[Route("api/instances")]
[Authorize(Roles = "Admin")]
public sealed class InstancesController : ControllerBase
{
    private readonly WebsiteMonitorDbContext _db;
    private readonly IInstanceRuntimeManager _runtime;

    public InstancesController(WebsiteMonitorDbContext db, IInstanceRuntimeManager runtime)
    {
        _db = db;
        _runtime = runtime;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var instances = await _db.Instances.OrderBy(i => i.InstanceId).ToListAsync();
        var result = instances.Select(i =>
        {
            _runtime.TryGet(i.InstanceId, out var s);
            return new
            {
                i.InstanceId,
                i.DisplayName,
                i.Enabled,
                i.TimeZoneId,
                i.CheckIntervalSeconds,
                i.ConcurrencyLimit,
                i.WriteHtmlSnapshot,
                i.OutputFolder,
                Runtime = new { State = s.State.ToString(), s.ChangedUtc }
            };
        });

        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Instance body)
    {
        // Minimal v1 create; validation is enforced in the UI wizard as well
        if (string.IsNullOrWhiteSpace(body.InstanceId) || string.IsNullOrWhiteSpace(body.DisplayName))
            return BadRequest("InstanceId and DisplayName are required.");

        var exists = await _db.Instances.AnyAsync(i => i.InstanceId == body.InstanceId);
        if (exists)
            return Conflict("InstanceId already exists.");

        body.CreatedUtc = DateTime.UtcNow;
        _db.Instances.Add(body);
        await _db.SaveChangesAsync();

        if (body.Enabled)
            await _runtime.StartAsync(body.InstanceId);

        return Created($"/api/instances/{body.InstanceId}", body);
    }

    [HttpPost("{instanceId}/start")]
    public async Task<IActionResult> Start(string instanceId)
    {
        await _runtime.StartAsync(instanceId);
        return Ok();
    }

    [HttpPost("{instanceId}/stop")]
    public async Task<IActionResult> Stop(string instanceId)
    {
        await _runtime.StopAsync(instanceId);
        return Ok();
    }

    [HttpPost("{instanceId}/restart")]
    public async Task<IActionResult> Restart(string instanceId)
    {
        await _runtime.RestartAsync(instanceId);
        return Ok();
    }
}
