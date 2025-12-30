namespace WebsiteMonitor.Monitoring.Checks;

public sealed record TargetCheckResult(
    string InstanceId,
    long TargetId,
    string Url,
    DateTime TimestampUtc,
    bool TcpOk,
    bool HttpOk,
    int? HttpStatusCode,
    int? TcpLatencyMs,
    int? HttpLatencyMs,
    string Summary);
