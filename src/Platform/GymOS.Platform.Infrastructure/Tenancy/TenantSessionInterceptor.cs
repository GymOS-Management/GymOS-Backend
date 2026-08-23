using System.Data;
using System.Data.Common;
using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GymOS.Platform.Infrastructure.Tenancy;

/// <summary>
/// Writes the acting tenant onto every database connection, so the row-level security predicate can
/// read it.
/// </summary>
/// <remarks>
/// <para><b>Written on every open, unconditionally — including a null value for platform scope.</b>
/// Nothing is ever read that this connection's current user did not just write, so a value cannot be
/// inherited from whichever request last borrowed the same physical connection.</para>
/// <para><b>This is defence in depth, not the only thing preventing that inheritance — measured, not
/// assumed.</b> SqlClient issues <c>sp_reset_connection</c> when a pooled connection is handed out
/// again, and that clears <c>SESSION_CONTEXT</c> along with everything else session-scoped. The
/// unconditional write is what keeps the isolation suite passing if that ever stops being true —
/// pooling behaviour is the driver's to change, and this control must not depend on it. The same
/// reasoning applied under Npgsql, whose <c>DISCARD ALL</c> on close cleared the session GUC the
/// PostgreSQL original used.</para>
/// <para>The shape this deliberately avoids is "set it when there is a tenant, clear it when there is
/// not": that has an execution path where neither branch runs, and it depends on the pool reset it
/// cannot see to stay correct.</para>
/// <para><b>NULL, not an empty string.</b> The PostgreSQL original wrote <c>''</c> because a session
/// GUC is text and <c>current_setting(…, true)</c> returns <c>''</c> for "set but empty", which the
/// policy then turned into NULL with <c>NULLIF</c>. <c>SESSION_CONTEXT</c> is <c>sql_variant</c> and
/// holds a real <c>uniqueidentifier</c>, so the empty-string round trip has nothing to model: writing
/// NULL directly is the same fail-closed outcome (a tenant-scoped table matches no rows under
/// platform scope, D4) with one fewer conversion that could throw. An empty string here would in fact
/// be worse — <c>CAST(N'' AS uniqueidentifier)</c> is a conversion error, not a NULL, so every query
/// under platform scope would fail rather than return nothing.</para>
/// <para><b>Provider-agnostic on purpose.</b> This takes <see cref="DbConnection"/> and never casts to
/// <c>SqlConnection</c>, unlike the PostgreSQL original which needed <c>NpgsqlConnection</c> to reach
/// positional parameters. Nothing here is SQL Server-specific except the procedure name.</para>
/// </remarks>
internal sealed class TenantSessionInterceptor : DbConnectionInterceptor
{
    /// <summary>The session-context key the RLS predicate function reads. Must match the migration.</summary>
    internal const string TenantKey = "app.tenant_id";

    private readonly ITenantContext _tenant;

    public TenantSessionInterceptor(ITenantContext tenant) => _tenant = tenant;

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await WriteTenantAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        WriteTenantAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }

    private async Task WriteTenantAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "sys.sp_set_session_context";

        AddParameter(command, "@key", DbType.String, TenantKey);

        // Typed as a Guid rather than a string so SESSION_CONTEXT stores a uniqueidentifier and the
        // predicate's CAST is a no-op rather than a parse of caller-influenced text.
        AddParameter(command, "@value", DbType.Guid, (object?)_tenant.TenantId ?? DBNull.Value);

        // 0, not 1: read_only would freeze the key for the whole session, and this connection is
        // pooled — the next request to borrow it could not write its own tenant and would silently
        // inherit this one, which is the exact failure this interceptor exists to prevent.
        AddParameter(command, "@read_only", DbType.Boolean, false);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
