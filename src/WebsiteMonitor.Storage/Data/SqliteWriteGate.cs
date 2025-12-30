namespace WebsiteMonitor.Storage.Data;

public static class SqliteWriteGate
{
    // SQLite supports multiple readers but only one writer at a time.
    // This prevents internal services from colliding (checks + alerting + identity writes).
    public static readonly SemaphoreSlim Gate = new(1, 1);
}
