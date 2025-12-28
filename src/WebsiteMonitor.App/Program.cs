using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.App.Infrastructure;
using WebsiteMonitor.Storage.Data;

var builder = WebApplication.CreateBuilder(args);

// DataRoot is configured in appsettings.Development.json for local dev.
// We require it in Step 2 so we don't write to unknown locations.
var dataRoot = builder.Configuration.GetValue<string>("WebsiteMonitor:DataRoot");
if (string.IsNullOrWhiteSpace(dataRoot))
{
    throw new InvalidOperationException("WebsiteMonitor:DataRoot is not set. Set it in appsettings.Development.json.");
}

var paths = new ProductPaths(dataRoot);

// Ensure directories exist
Directory.CreateDirectory(Path.GetDirectoryName(paths.DbPath)!);
Directory.CreateDirectory(paths.DataProtectionKeysDir);

// EF Core SQLite
builder.Services.AddDbContext<WebsiteMonitorDbContext>(options =>
{
    options.UseSqlite($"Data Source={paths.DbPath}");
});

// Health checks
builder.Services.AddHealthChecks();


// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
// Apply migrations on startup (dev convenience for now)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WebsiteMonitorDbContext>();
    db.Database.Migrate();
}

app.MapHealthChecks("/healthz");


// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast")
.WithOpenApi();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
