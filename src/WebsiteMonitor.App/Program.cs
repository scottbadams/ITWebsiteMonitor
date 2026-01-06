using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebsiteMonitor.App.Infrastructure;
using WebsiteMonitor.App.Snapshots;
using WebsiteMonitor.Storage.Data;
using WebsiteMonitor.Storage.Identity;
using WebsiteMonitor.Monitoring.HostedServices;
using WebsiteMonitor.Monitoring.Runtime;
using WebsiteMonitor.Monitoring.Alerting;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Force Kestrel to listen on all interfaces (NIC), not just localhost.
// This keeps existing ports/schemes (from launchSettings / --urls / env vars) but replaces host=localhost/loopback with 0.0.0.0.
// Network access is still controlled separately by the AllowNetworkAccess middleware.
static string NormalizeUrlsToAnyIp(string urls)
{
    if (string.IsNullOrWhiteSpace(urls)) return urls;

    var parts = urls.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    for (int i = 0; i < parts.Length; i++)
    {
        var u = parts[i];

        // Handle wildcard formats quickly
        if (u.Contains("://*:", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("://0.0.0.0:", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("://[::]:", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (Uri.TryCreate(u, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase))
            {
                // Preserve scheme + port + path/query if any (should usually be none).
                var ub = new UriBuilder(uri) { Host = "0.0.0.0" };
                parts[i] = ub.Uri.ToString().TrimEnd('/');
            }
            else
            {
                parts[i] = uri.ToString().TrimEnd('/');
            }
        }
    }

    return string.Join(';', parts);
}

// Try to read the URLs Kestrel would bind to. If not present, default to the app's known dev URL.
var rawUrls =
    builder.Configuration[WebHostDefaults.ServerUrlsKey] ??
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ??
    "http://localhost:5041";

builder.WebHost.UseUrls(NormalizeUrlsToAnyIp(rawUrls));



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

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(paths.DataProtectionKeysDir))
    .SetApplicationName("ITWebsiteMonitor");

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<WebsiteMonitor.App.Infrastructure.SmtpPasswordProtector>();
builder.Services.AddHttpClient<WebsiteMonitor.Notifications.Webhooks.IWebhookSender,
    WebsiteMonitor.Notifications.Webhooks.HttpClientWebhookSender>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
});

// --------------------
// Services
// --------------------

// EF Core SQLite
builder.Services.AddSingleton<SqlitePragmaConnectionInterceptor>();

builder.Services.AddScoped<WebsiteMonitor.Notifications.Smtp.ISmtpSender, WebsiteMonitor.Notifications.Smtp.MailKitSmtpSender>();

builder.Services.AddSingleton<WebsiteMonitor.Notifications.ISmtpEmailSender, WebsiteMonitor.Notifications.MailKitSmtpEmailSender>();

builder.Services.AddDbContext<WebsiteMonitorDbContext>((sp, options) =>
{
    options.UseSqlite($"Data Source={paths.DbPath}");
    options.AddInterceptors(sp.GetRequiredService<SqlitePragmaConnectionInterceptor>());
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

// Adds per-request claims (theme + group-role projection).
builder.Services.AddScoped<IClaimsTransformation, GroupRoleClaimsTransformation>();

// Allow auth cookies to be sent from inside an iframe (e.g., Zabbix URL widget).
// Requires HTTPS for SameSite=None; Secure cookies.
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.IsEssential = true;
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.IsEssential = true;
});

builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.MinimumSameSitePolicy = SameSiteMode.Unspecified;
    options.OnAppendCookie = ctx =>
    {
        if (ctx.CookieOptions.SameSite == SameSiteMode.None)
        {
            ctx.CookieOptions.Secure = true;
        }
    };
    options.OnDeleteCookie = ctx =>
    {
        if (ctx.CookieOptions.SameSite == SameSiteMode.None)
        {
            ctx.CookieOptions.Secure = true;
        }
    };
});


// Authorization services (needed for [Authorize])
builder.Services.AddAuthorization();

// Razor Pages (we will use this for /bootstrap and /setup pages)
builder.Services.AddRazorPages();


// Trust reverse-proxy headers so Request.IsHttps and client IP are correct behind TLS termination.
// NOTE: This currently trusts forwarded headers from any proxy. For best security, restrict KnownProxies/KnownNetworks.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddControllers();
builder.Services.AddSingleton<WebsiteMonitor.Monitoring.Checks.TargetCheckService>();

builder.Services.Configure<SnapshotOptions>(builder.Configuration.GetSection("WebsiteMonitor:Snapshots"));
builder.Services.AddHostedService<HtmlSnapshotHostedService>();

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
builder.Services.Configure<AlertingOptions>(builder.Configuration.GetSection("WebsiteMonitor:Alerting"));
builder.Services.AddSingleton<TimeZoneResolver>();
builder.Services.AddScoped<AlertEvaluator>();
builder.Services.AddHostedService<AlertSchedulerHostedService>();

// --------------------
// Build
// --------------------
var app = builder.Build();


// Apply forwarded headers (X-Forwarded-Proto/For/Host) before auth/antiforgery.
app.UseForwardedHeaders();
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


// Allow this site to be embedded by Zabbix (monitor.itgreatfalls.com).
// This is required for the URL widget / iframe.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        if (!ctx.Response.Headers.ContainsKey("Content-Security-Policy"))
        {
            ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self' https://monitor.itgreatfalls.com";
        }
        return Task.CompletedTask;
    });

    await next();
});



// Block non-local access unless enabled in System Settings.
// This is evaluated per-request (no restart needed).
app.Use(async (ctx, next) =>
{
    var ip = ctx.Connection.RemoteIpAddress;

    // Allow localhost/loopback and unknown (paranoia-safe for dev)
    if (ip == null || IPAddress.IsLoopback(ip))
    {
        await next();
        return;
    }

    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebsiteMonitorDbContext>();

        // If no SystemSettings row exists yet, treat as disabled (secure default).
        var allow = await db.SystemSettings
            .AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.AllowNetworkAccess)
            .FirstOrDefaultAsync();

        if (!allow)
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync("<!doctype html><html><head><meta charset=\"utf-8\"><title>403 Forbidden</title></head><body style=\"font-family:Segoe UI, Arial, sans-serif; padding:24px;\"><h2>Network access is disabled</h2><p>This WebsiteMonitor instance is configured to allow access only from <b>localhost</b>.</p><p>To enable network access, open <code>http://localhost:5041/setup</code> locally and turn on <b>Allow network access</b> in <b>System Settings</b>.</p></body></html>");
            return;
        }
    }
    catch
    {
        // If DB isn't ready (e.g., migration not applied yet), do not lock out access.
        await next();
        return;
    }

    await next();
});

app.UseStaticFiles();
app.UseCookiePolicy();
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