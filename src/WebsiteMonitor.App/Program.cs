using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebsiteMonitor.App.Infrastructure;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Identity;
using WebsiteMonitor.Monitoring.HostedServices;
using WebsiteMonitor.Monitoring.Runtime;

var builder = WebApplication.CreateBuilder(args);

// --------------------
// Required config (local dev)
// --------------------
var dataRoot = builder.Configuration.GetValue<string>("WebsiteMonitor:DataRoot");
if (string.IsNullOrWhiteSpace(dataRoot))
{
    throw new InvalidOperationException(
        "WebsiteMonitor:DataRoot is not set. Set it in src/WebsiteMonitor.App/appsettings.Development.json.");
}

var paths = new ProductPaths(dataRoot);

// Ensure directories exist
Directory.CreateDirectory(Path.GetDirectoryName(paths.DbPath)!);
Directory.CreateDirectory(paths.DataProtectionKeysDir);

// --------------------
// Services
// --------------------

// EF Core SQLite
builder.Services.AddDbContext<WebsiteMonitorDbContext>(options =>
{
    options.UseSqlite($"Data Source={paths.DbPath}");
});

// Health checks
builder.Services.AddHealthChecks();

// Identity (local accounts + roles)
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<WebsiteMonitorDbContext>()
    .AddDefaultTokenProviders();

// Authorization services (needed for [Authorize])
builder.Services.AddAuthorization();

// Razor Pages (we will use this for /bootstrap and /setup pages)
builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddSingleton<WebsiteMonitor.Monitoring.Checks.TargetCheckService>();

builder.Services.AddHttpClient("monitor")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Reasonable defaults; can tune later
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 50
    });

// Swagger/OpenAPI (keep template stuff for now)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<IInstanceRuntimeManager, InstanceRuntimeManager>();
builder.Services.AddHostedService<InstanceAutoStartHostedService>();

// --------------------
// Build
// --------------------
var app = builder.Build();

// --------------------
// Database migrate + create roles on startup
// --------------------
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WebsiteMonitorDbContext>();
    await db.Database.MigrateAsync();

    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var roles = new[] { "Admin", "Viewer" };
    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new IdentityRole(role));
        }
    }
}

// --------------------
// Middleware / endpoints
// --------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapHealthChecks("/healthz");
app.MapControllers();

// Keep the template endpoint if you want; harmless for now.
var summaries = new[]
{
    "Freezing","Bracing","Chilly","Cool","Mild","Warm","Balmy","Hot","Sweltering","Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast(
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast")
.WithOpenApi();

app.MapGet("/setup/setup-{instanceId}", (string instanceId) =>
        Results.Redirect($"/setup/instances/{instanceId}"))
   .RequireAuthorization(p => p.RequireRole("Admin"));

app.MapGet("/monitor-{instanceId}", (string instanceId) =>
        Results.Redirect($"/monitor/{instanceId}"));

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
