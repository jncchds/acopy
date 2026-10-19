using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ACopy.Api.Data;

/// <summary>
/// Sets SQLite's own busy handler on every connection. Microsoft.Data.Sqlite retries busy statements but not
/// COMMIT, so concurrent writers (runs finishing together, the label scan) would otherwise fail with
/// "database is locked" in rollback-journal mode.
/// </summary>
public sealed class BusyTimeoutInterceptor : DbConnectionInterceptor
{
    private const string Pragma = "PRAGMA busy_timeout = 15000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragma;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragma;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
