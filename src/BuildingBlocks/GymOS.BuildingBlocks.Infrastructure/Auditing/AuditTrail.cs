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
            UserEmail = _currentUser.Email ?? subjectEmail,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            NewValues = details is null || details.Count == 0
                ? null
                : JsonSerializer.Serialize(details, DetailSerializerOptions),
            CorrelationId = _correlationContext.CorrelationId,
            CreatedAtUtc = _clock.UtcNow,
        });
    }
}
