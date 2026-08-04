using System.Globalization;
using System.Text.Json;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.SharedKernel.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GymOS.BuildingBlocks.Infrastructure.Auditing;

/// <summary>
/// Turns row mutations into audit entries during SaveChanges (AD-7) — the same transaction
/// that commits the change commits its trail, so the two cannot diverge.
///
/// Only declared entities are captured (see <see cref="AuditPolicy"/>), and only scalar
/// properties: navigations would recurse, and shadow state like <c>xmin</c> describes the
/// storage engine rather than the business fact. Modifications record just the properties
/// that actually changed, because a diff listing forty unchanged fields is a diff nobody
/// reads.
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions ValueSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IClock _clock;
    private readonly ICurrentUser _currentUser;
    private readonly ICorrelationContext _correlationContext;
    private readonly AuditPolicy _policy;

    public AuditSaveChangesInterceptor(
        IClock clock,
        ICurrentUser currentUser,
        ICorrelationContext correlationContext,
        AuditPolicy policy)
    {
        _clock = clock;
        _currentUser = currentUser;
        _correlationContext = correlationContext;
        _policy = policy;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        CaptureAuditEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        CaptureAuditEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void CaptureAuditEntries(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        DateTimeOffset capturedAtUtc = _clock.UtcNow;

        // Materialize before writing: adding audit rows mutates the change tracker, and
        // the audit rows themselves are not audited (they are not declared entities).
        var auditable = context.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => (Entry: entry, Declared: _policy.For(entry.Entity.GetType())))
            .Where(candidate => candidate.Declared is not null)
            .ToList();

        foreach ((EntityEntry entry, AuditedEntity? declared) in auditable)
        {
            AuditLog? auditLog = BuildAuditEntry(entry, declared!, capturedAtUtc);

            if (auditLog is not null)
            {
                context.Set<AuditLog>().Add(auditLog);
            }
        }
    }

    private AuditLog? BuildAuditEntry(EntityEntry entry, AuditedEntity declared, DateTimeOffset capturedAtUtc)
    {
        (string suffix, Dictionary<string, string?>? oldValues, Dictionary<string, string?>? newValues) =
            entry.State switch
            {
                EntityState.Added => ("created", null, Snapshot(entry, declared, current: true)),
                EntityState.Deleted => ("deleted", Snapshot(entry, declared, current: false), null),
                _ => ("updated", ChangedOnly(entry, declared, current: false), ChangedOnly(entry, declared, current: true)),
            };

        // A "modification" that changed nothing but the row timestamp is not an event worth a
        // permanent record. This covers two real cases: a save where no value actually differed,
        // and a deliberate version bump on an aggregate whose children changed — the child rows
        // carry their own entries, so a parent entry holding one timestamp adds only noise.
        if (entry.State == EntityState.Modified && CarriesNoBusinessChange(newValues))
        {
            return null;
        }

        return new AuditLog
        {
            UserId = _currentUser.UserId,
            UserEmail = _currentUser.Email,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            Action = $"{declared.Name}.{suffix}",
            EntityType = declared.Name,
            EntityId = PrimaryKeyOf(entry),
            OldValues = Serialize(oldValues),
            NewValues = Serialize(newValues),
            CorrelationId = _correlationContext.CorrelationId,
            CreatedAtUtc = capturedAtUtc,
        };
    }

    private static bool CarriesNoBusinessChange(Dictionary<string, string?>? newValues) =>
        newValues is null or { Count: 0 } ||
        (newValues.Count == 1 && newValues.ContainsKey(nameof(IAuditable.UpdatedAtUtc)));

    private Dictionary<string, string?> Snapshot(EntityEntry entry, AuditedEntity declared, bool current) =>
        entry.Properties
            .Where(property => IsCapturable(property))
            .ToDictionary(
                property => property.Metadata.Name,
                property => Render(declared, property, current),
                StringComparer.Ordinal);

    private Dictionary<string, string?> ChangedOnly(EntityEntry entry, AuditedEntity declared, bool current) =>
        entry.Properties
            .Where(property => IsCapturable(property) && property.IsModified && HasChanged(property))
            .ToDictionary(
                property => property.Metadata.Name,
                property => Render(declared, property, current),
                StringComparer.Ordinal);

    /// <summary>
    /// EF marks a property modified when it was *assigned*, not when the value differs.
    /// Comparing keeps "saved the same value again" out of the trail.
    /// </summary>
    private static bool HasChanged(PropertyEntry property) =>
        !Equals(property.OriginalValue, property.CurrentValue);

    private static bool IsCapturable(PropertyEntry property) =>
        !property.Metadata.IsShadowProperty() && !property.Metadata.IsConcurrencyToken;

    private string? Render(AuditedEntity declared, PropertyEntry property, bool current)
    {
        if (_policy.ShouldRedact(declared.ClrType, property.Metadata.Name))
        {
            return AuditPolicy.RedactedMarker;
        }

        object? value = current ? property.CurrentValue : property.OriginalValue;

        return value switch
        {
            null => null,
            string text => text,
            DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    private static Guid? PrimaryKeyOf(EntityEntry entry)
    {
        PropertyEntry? key = entry.Properties.FirstOrDefault(property => property.Metadata.IsPrimaryKey());
        return key?.CurrentValue is Guid id ? id : null;
    }

    private static string? Serialize(Dictionary<string, string?>? values) =>
        values is null || values.Count == 0
            ? null
            : JsonSerializer.Serialize(values, ValueSerializerOptions);
}
