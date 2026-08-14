using System.Data.Common;
using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace GymOS.Platform.Infrastructure.Tenancy;

/// <summary>
/// Writes the acting tenant onto every database connection, so the row-level security policies can
/// read it.
/// </summary>
/// <remarks>
/// <para><b>Written on every open, unconditionally — including an empty value for platform scope.</b>
/// Nothing is ever read that this connection's current user did not just write, so a value cannot be
/// inherited from whichever request last borrowed the same physical connection.</para>
/// <para><b>This is defence in depth, not the only thing preventing that inheritance — measured, not
/// assumed.</b> Npgsql resets a pooled connection on close (<c>DISCARD ALL</c>/<c>RESET ALL</c>), which
/// already clears a session GUC set by <c>set_config(…, false)</c>. That reset is switched off by
/// <c>No Reset On Close=true</c>, and is not performed at all when multiplexing is enabled; neither is
/// set anywhere in this solution today, and either could be set by a future connection-string change
/// made for unrelated reasons. Changing <see cref="WriteTenantAsync"/> to return early when there is no
/// tenant — the "set it when there is a tenant" shape — was tried against the isolation suite and all
/// eight tests still passed, because the pool's own reset covers the gap under today's settings. The
/// unconditional write is what keeps them passing when it does not.</para>
/// <para>The shape this deliberately avoids is "set it when there is a tenant, reset it when there is
/// not": that has an execution path where neither branch runs, and it depends on the pool reset it
/// cannot see to stay correct.</para>
/// <para><c>SET LOCAL</c> would also be immune, being transaction-scoped, and is not used because EF
/// issues plain reads outside a transaction: it would either silently not apply or force a
/// transaction around every query.</para>
/// </remarks>
internal sealed class TenantSessionInterceptor : DbConnectionInterceptor
{
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
        // An empty string, not NULL and not a skipped write: `current_setting('app.tenant_id', true)`
        // then yields '', which casts to NULL for the comparison, so a tenant-scoped table matches no
        // rows under platform scope. Fail closed (D4).
        string value = _tenant.TenantId?.ToString() ?? string.Empty;

        await using NpgsqlCommand command = ((NpgsqlConnection)connection).CreateCommand();
        command.CommandText = "SELECT set_config('app.tenant_id', $1, false)";
        command.Parameters.Add(new NpgsqlParameter { Value = value });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
