using System.Text.Json;
using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace GymOS.BuildingBlocks.Infrastructure.Auditing;

/// <summary>
/// Explicit audit entries (AD-7) for actions no row diff would describe. Rows are added to
/// the context and left for the caller's <c>SaveChangesAsync</c>, so the entry commits with
/// the action it records — or not at all.
/// </summary>
public sealed class AuditTrail : IAuditTrail
{
    private static readonly JsonSerializerOptions DetailSerializerOptions = new(JsonSerializerDefaults.Web);

    // Mirrors AuditLogConfiguration's column widths. Truncation happens HERE rather than being
    // left to the database, because several of these values arrive straight from an unauthenticated
    // caller — a failed sign-in records the address it was attempted against, and an over-long one
    // used to make the whole save throw (Postgres 22001), turning a 401 into a 500 and losing the
    // very audit row that documents the attempt. An audit entry must never be the reason a request
    // fails; a truncated fact is worth more than an exception.
    private const int UserEmailMaxLength = 320;
    private const int IpAddressMaxLength = 45;
    private const int UserAgentMaxLength = 512;
    private const int ActionMaxLength = 100;
    private const int EntityTypeMaxLength = 100;

    private readonly DbContext _dbContext;
    private readonly IClock _clock;
    private readonly ICurrentUser _currentUser;
    private readonly ICorrelationContext _correlationContext;

    public AuditTrail(
        DbContext dbContext,
        IClock clock,
        ICurrentUser currentUser,
        ICorrelationContext correlationContext)
    {
        _dbContext = dbContext;
        _clock = clock;
        _currentUser = currentUser;
        _correlationContext = correlationContext;
    }

    public void Record(
        string action,
        string? entityType = null,
        Guid? entityId = null,
        IReadOnlyDictionary<string, string?>? details = null,
        string? subjectEmail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        _dbContext.Set<AuditLog>().Add(new AuditLog
        {
            UserId = _currentUser.UserId,

            // Falls back to the subject when nobody is authenticated — a failed sign-in
            // has no acting user, and the address it was attempted against is the fact
            // worth keeping.
            UserEmail = Fit(_currentUser.Email ?? subjectEmail, UserEmailMaxLength),
            IpAddress = Fit(_currentUser.IpAddress, IpAddressMaxLength),
            UserAgent = Fit(_currentUser.UserAgent, UserAgentMaxLength),
            Action = Fit(action, ActionMaxLength)!,
            EntityType = Fit(entityType, EntityTypeMaxLength),
            EntityId = entityId,
            NewValues = details is null || details.Count == 0
                ? null
                : JsonSerializer.Serialize(details, DetailSerializerOptions),
            CorrelationId = _correlationContext.CorrelationId,
            CreatedAtUtc = _clock.UtcNow,
        });
    }

    /// <summary>
    /// Trims a value to its column width. Truncated rather than rejected: the row exists to
    /// record that something happened, and losing the whole record over a long string would
    /// hide exactly the requests most worth seeing.
    /// </summary>
    private static string? Fit(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
