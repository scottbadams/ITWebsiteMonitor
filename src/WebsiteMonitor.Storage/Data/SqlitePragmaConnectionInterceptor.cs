using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WebsiteMonitor.Storage.Data;

public sealed class SqlitePragmaConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ILogger<SqlitePragmaConnectionInterceptor> _logger;

    public SqlitePragmaConnectionInterceptor(ILogger<SqlitePragmaConnectionInterceptor> logger)
    {
        _logger = logger;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyPragmasAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void ApplyPragmas(DbConnection connection)
    {
        if (connection is not SqliteConnection) return;

        try
        {
            using var cmd = connection.CreateCommand();

            // WAL improves concurrent read/write behavior.
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();

            // Wait when DB is busy/locked instead of failing immediately.
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();

            // Reasonable durability/perf tradeoff for monitoring data.
            cmd.CommandText = "PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply SQLite PRAGMAs.");
        }
    }

    private async Task ApplyPragmasAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection is not SqliteConnection) return;

        try
        {
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            await cmd.ExecuteNonQueryAsync(ct);

            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            await cmd.ExecuteNonQueryAsync(ct);

            cmd.CommandText = "PRAGMA synchronous=NORMAL;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply SQLite PRAGMAs (async).");
        }
    }
}
