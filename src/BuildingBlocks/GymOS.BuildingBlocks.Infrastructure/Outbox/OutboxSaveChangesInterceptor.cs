using System.Text.Json;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.SharedKernel.Entities;
using GymOS.SharedKernel.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GymOS.BuildingBlocks.Infrastructure.Outbox;

/// <summary>
/// Drains raised domain events from tracked aggregates into <see cref="OutboxMessage"/>
/// rows during SaveChanges — the same transaction commits state, audit, and events
/// atomically (AD-13). Envelope: identity/name/version from the event, aggregate
/// id/type from the raising aggregate, occurred-at from <see cref="IClock"/> at
/// capture (the edge — AD-40); acting user and correlation id from the request context.
/// Tenant and causation depth are enriched when the tenant and automation seams arrive;
/// the columns are nullable by design — platform events carry no tenant, system
/// actions no user (AD-14).
/// </summary>
public sealed class OutboxSaveChangesInterceptor : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IClock _clock;
    private readonly ICorrelationContext _correlationContext;
    private readonly ICurrentUser _currentUser;

    public OutboxSaveChangesInterceptor(
        IClock clock,
        ICorrelationContext correlationContext,
        ICurrentUser currentUser)
    {
        _clock = clock;
        _correlationContext = correlationContext;
        _currentUser = currentUser;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        CaptureDomainEvents(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        CaptureDomainEvents(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void CaptureDomainEvents(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        DateTimeOffset capturedAtUtc = _clock.UtcNow;

        // Materialize first: adding outbox rows mutates the change tracker.
        var aggregatesWithEvents = context.ChangeTracker
            .Entries<AggregateRoot>()
            .Where(entry => entry.Entity.DomainEvents.Count > 0)
            .Select(entry => entry.Entity)
            .ToList();

        foreach (AggregateRoot aggregate in aggregatesWithEvents)
        {
            foreach (DomainEvent domainEvent in aggregate.DequeueDomainEvents())
            {
                context.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    EventId = domainEvent.EventId,
                    EventName = domainEvent.EventName,
                    EventVersion = domainEvent.EventVersion,
                    OccurredAtUtc = capturedAtUtc,
                    AggregateType = aggregate.GetType().Name,
                    AggregateId = aggregate.Id,
                    UserId = _currentUser.UserId,
                    CorrelationId = _correlationContext.CorrelationId,
                    CausationDepth = 0,
                    Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), PayloadSerializerOptions),
                    CreatedAtUtc = capturedAtUtc,
                });
            }
        }
    }
}
