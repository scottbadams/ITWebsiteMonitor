namespace WebsiteMonitor.Storage.Models;

public sealed class SystemSettings
{
    public int Id { get; set; } = 1;

    /// <summary>
    /// Logo path/URL. Examples: "~/images/itgreatfalls-logo.png" or "/images/logo.png".
    /// </summary>
    public string? LogoPath { get; set; }

    public string? DefaultTimeZoneId { get; set; }

    public int? DefaultCheckIntervalSeconds { get; set; }

    public int? DefaultConcurrencyLimit { get; set; }

    /// <summary>
    /// Snapshot output folder template. Use "{instanceId}" as a placeholder.
    /// Example: "Snapshots/{instanceId}"
    /// </summary>
    public string? DefaultSnapshotOutputFolderTemplate { get; set; }


    /// <summary>
    /// When false, non-loopback HTTP requests are blocked (403).
    /// </summary>
    public bool AllowNetworkAccess { get; set; } = false;

    /// <summary>
    /// Absolute base URL used for links in alert emails and snapshot HTML.
    /// Example: http://localhost:5041 or https://monitor.example.com  (no trailing slash required)
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}
