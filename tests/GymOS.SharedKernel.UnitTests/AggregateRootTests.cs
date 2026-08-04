using GymOS.SharedKernel.Entities;
using GymOS.SharedKernel.Events;
using Xunit;

namespace GymOS.SharedKernel.UnitTests;

public sealed class AggregateRootTests
{
    private sealed record SomethingHappened(string Detail) : DomainEvent
    {
        public override string EventName => "gymos.tests.something.happened";
    }

    private sealed class TestAggregate : AggregateRoot
    {
        public TestAggregate()
            : base(Guid.NewGuid())
        {
        }

        public void DoSomething(string detail) => Raise(new SomethingHappened(detail));
    }

    [Fact]
    public void Raise_CollectsEventsInOrder()
    {
        var aggregate = new TestAggregate();

        aggregate.DoSomething("first");
        aggregate.DoSomething("second");

        Assert.Collection(
            aggregate.DomainEvents,
            e => Assert.Equal("first", Assert.IsType<SomethingHappened>(e).Detail),
            e => Assert.Equal("second", Assert.IsType<SomethingHappened>(e).Detail));
    }

    [Fact]
    public void DequeueDomainEvents_ReturnsAllAndClears()
    {
        var aggregate = new TestAggregate();
        aggregate.DoSomething("only");

        var dequeued = aggregate.DequeueDomainEvents();

        Assert.Single(dequeued);
        Assert.Empty(aggregate.DomainEvents);
        Assert.Empty(aggregate.DequeueDomainEvents());
    }

    [Fact]
    public void EveryEventOccurrence_HasAUniqueEventId()
    {
        var aggregate = new TestAggregate();
        aggregate.DoSomething("same");
        aggregate.DoSomething("same");

        var events = aggregate.DequeueDomainEvents();

        Assert.NotEqual(events[0].EventId, events[1].EventId);
    }

    [Fact]
    public void EventVersion_DefaultsToOne()
    {
        var domainEvent = new SomethingHappened("x");

        Assert.Equal(1, domainEvent.EventVersion);
        Assert.Equal("gymos.tests.something.happened", domainEvent.EventName);
    }
}
