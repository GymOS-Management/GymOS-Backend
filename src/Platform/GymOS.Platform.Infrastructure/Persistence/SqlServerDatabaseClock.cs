using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>
/// SQL Server's <c>SYSUTCDATETIME()</c> as <see cref="IDatabaseClock"/> — one round trip per ask.
///
/// <para>The PostgreSQL original read <c>now()</c>, which is <c>timestamptz</c> and therefore carries
/// its own offset. SQL Server's equivalent pair is <c>SYSDATETIMEOFFSET()</c> (server-local wall clock
/// plus that server's offset) and <c>SYSUTCDATETIME()</c> (UTC, no offset). This uses the latter and
/// attaches <see cref="TimeSpan.Zero"/>, so the value is UTC by construction rather than by trusting
/// the server's time zone to be UTC — <c>SYSDATETIMEOFFSET()</c> on a host set to Europe/Nicosia
/// returns the same instant with a +02:00/+03:00 offset, and every durable verdict this clock decides
/// (Overdue, Expired, a billed renewal) would then be compared against a differently-shaped value
/// depending on where the database happened to be provisioned.</para>
/// </summary>
internal sealed class SqlServerDatabaseClock : IDatabaseClock
{
    private readonly AppDbContext _dbContext;

    public SqlServerDatabaseClock(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<DateTimeOffset> UtcNowAsync(CancellationToken cancellationToken)
    {
        DateTime utcNow = await _dbContext.Database
            .SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]")
            .SingleAsync(cancellationToken);

        return new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }
}
