using System.Reflection;
using System.Text.RegularExpressions;
using GymOS.SharedKernel.Events;
using Xunit;

namespace GymOS.ArchitectureTests;

/// <summary>
/// Rule R9: every domain event is versioned, named by convention, and unique
/// (ARCHITECTURE-EXTENSIONS.md §1.2, AD-14).
///
/// <para>Event names are the most permanent contract the platform has. Once an event has been
/// published, a name lives in the outbox, in consumers, and in whatever a broker retained — so a
/// rename is a versioned migration, not an edit. That makes naming worth enforcing at build time
/// rather than at review time, and it is the reason these run over every production assembly instead
/// of over a list someone maintains.</para>
/// </summary>
public sealed partial class DomainEventConventionTests
{
    /// <summary><c>gymos.&lt;module&gt;.&lt;aggregate&gt;.&lt;action&gt;</c>, lower snake_case.</summary>
    [GeneratedRegex(@"^gymos\.[a-z0-9_]+\.[a-z0-9_]+\.[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EventNamePattern();

    public static TheoryData<Type> AllDomainEvents()
    {
        var data = new TheoryData<Type>();

        foreach (Type eventType in DomainEventTypes())
        {
            data.Add(eventType);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllDomainEvents))]
    public void EveryEventNameFollowsTheConvention(Type eventType)
    {
        DomainEvent instance = Instantiate(eventType);

        Assert.True(
            EventNamePattern().IsMatch(instance.EventName),
            $"{eventType.Name} publishes '{instance.EventName}', which is not " +
            "gymos.<module>.<aggregate>.<action> in lower snake_case.");
    }

    [Theory]
    [MemberData(nameof(AllDomainEvents))]
    public void EveryEventIsVersionedFromOne(Type eventType)
    {
        DomainEvent instance = Instantiate(eventType);

        // A version of zero or below cannot be incremented meaningfully, and consumers key their
        // payload handling on it (AD-14).
        Assert.True(
            instance.EventVersion >= 1,
            $"{eventType.Name} declares EventVersion {instance.EventVersion}; versions start at 1.");
    }

    [Theory]
    [MemberData(nameof(AllDomainEvents))]
    public void EveryEventCarriesItsOwnIdentity(Type eventType)
    {
        // EventId is the handler idempotency key (AD-39). Two instances sharing one would make
        // at-least-once delivery indistinguishable from a duplicate.
        Assert.NotEqual(Instantiate(eventType).EventId, Instantiate(eventType).EventId);
    }

    [Fact]
    public void EventNamesAreUniqueAcrossThePlatform()
    {
        var duplicates = DomainEventTypes()
            .Select(type => new { Type = type, Name = Instantiate(type).EventName })
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ({string.Join(", ", group.Select(entry => entry.Type.Name))})")
            .ToList();

        // A shared name makes a consumer's subscription ambiguous, and no runtime error would say so.
        Assert.True(duplicates.Count == 0, "Duplicate event names: " + string.Join("; ", duplicates));
    }

    [Fact]
    public void EventNamesUseThePastTense()
    {
        // Convention, not grammar policing: an event is a fact that already happened, and a
        // present-tense name reads as a command. The check looks for a past participle anywhere in
        // the action, because a correct name need not end with one — `locked_out` and
        // `role_assigned` both are past tense, in different positions. That still catches the
        // realistic mistake (`user.create` for `user.created`).
        var presentTense = DomainEventTypes()
            .Select(type => Instantiate(type).EventName)
            .Where(name => !name.Split('.')[^1]
                .Split('_')
                .Any(word =>
                    word.EndsWith("ed", StringComparison.Ordinal) ||
                    word.EndsWith("en", StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            presentTense.Count == 0,
            "Event names should be past tense (something happened): " + string.Join(", ", presentTense));
    }

    [Fact]
    public void EveryEventIsAnImmutableRecord()
    {
        // Events are facts: a mutable one could be altered between capture and delivery, and the
        // outbox payload would then disagree with what a handler sees.
        var mutable = DomainEventTypes()
            .Where(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.SetMethod is { IsPublic: true, ReturnParameter.IsIn: false } setter
                    && !setter.IsInitOnly()))
            .Select(type => type.Name)
            .ToList();

        Assert.True(mutable.Count == 0, "Events with settable properties: " + string.Join(", ", mutable));
    }

    [Fact]
    public void TheEventCatalogIsNotEmpty()
    {
        // Guards the tests above: a reflection query that silently finds nothing would make every
        // other assertion here vacuously true.
        Assert.NotEmpty(DomainEventTypes());
    }

    private static List<Type> DomainEventTypes() =>
        ProductionAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsInterface: false } && typeof(DomainEvent).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

    private static Assembly[] ProductionAssemblies() =>
    [
        typeof(GymOS.SharedKernel.AssemblyMarker).Assembly,
        typeof(GymOS.Platform.Domain.AssemblyMarker).Assembly,
        typeof(GymOS.Platform.Application.AssemblyMarker).Assembly,
    ];

    /// <summary>
    /// Builds an event with default arguments. Events are records over data, so their constructors
    /// take only values — which is what makes reflecting over the whole catalog viable.
    /// </summary>
    private static DomainEvent Instantiate(Type eventType)
    {
        ConstructorInfo constructor = eventType.GetConstructors()
            .OrderBy(candidate => candidate.GetParameters().Length)
            .First();

        object?[] arguments = constructor.GetParameters()
            .Select(parameter => parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(parameter.ParameterType)
                : null)
            .ToArray();

        return (DomainEvent)constructor.Invoke(arguments);
    }
}

internal static class SetterExtensions
{
    /// <summary>
    /// Whether a setter is <c>init</c> rather than <c>set</c>. Reflection reports both as a set
    /// method; only the <c>IsExternalInit</c> modifier tells them apart.
    /// </summary>
    public static bool IsInitOnly(this MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");
}
