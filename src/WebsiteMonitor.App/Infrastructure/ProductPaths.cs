namespace WebsiteMonitor.App.Infrastructure;

public sealed class ProductPaths
{
    public string DataRoot { get; }
    public string DbPath { get; }
    public string DataProtectionKeysDir { get; }

    public ProductPaths(string dataRoot)
    {
        DataRoot = dataRoot;
        DbPath = Path.Combine(DataRoot, "db", "WebsiteMonitor.sqlite");
        DataProtectionKeysDir = Path.Combine(DataRoot, "dpkeys");
    }
}
