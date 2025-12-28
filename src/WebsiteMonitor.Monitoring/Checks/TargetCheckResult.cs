namespace WebsiteMonitor.Monitoring.Checks;

public sealed record TargetCheckResult(
    long TargetId,
    DateTime TimestampUtc,
    bool TcpOk,
    bool HttpOk,
    int? HttpStatusCode,
    int? TcpLatencyMs,
    int? HttpLatencyMs,
    string Summary);
