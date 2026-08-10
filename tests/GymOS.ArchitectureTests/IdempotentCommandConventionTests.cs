using System.Collections;
using System.Reflection;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.ArchitectureTests;

/// <summary>
/// AD-39 guardrails: an idempotent command must be a command (never a query — replaying a
/// read is meaningless) and its response must be Result-shaped, or the pipeline could not
/// store and replay its outcome. The behavior's TResponse : Result constraint silently skips
/// non-conforming types; this test turns that silence into a build failure.
/// </summary>
public sealed class IdempotentCommandConventionTests
{
    public static TheoryData<Type> AllIdempotentCommands()
    {
        var data = new TheoryData<Type>();
        foreach (Type type in ProductionAssemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(IIdempotentCommand).IsAssignableFrom(type)
                && type is { IsInterface: false, IsAbstract: false }))
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void The_theories_below_are_not_vacuous()
    {
        // MarkInvoicePaidCommand (M3.3) is the first production IIdempotentCommand, so discovery
        // is expected to find at least one. The seeded exemplars that kept the theories alive
        // before it landed are gone — going back to zero would mean the theories silently stopped
        // checking anything, which is exactly the failure mode this fact exists to catch.
        Assert.True(AllIdempotentCommands().Count > 0,
            "No IIdempotentCommand found in production assemblies. If the last one was removed " +
            "deliberately, reinstate seeded exemplars here (see git history) so the theories stay " +
            "exercised; an empty MemberData errors on this runner rather than skipping.");
    }

    [Theory]
    [MemberData(nameof(AllIdempotentCommands))]
    public void Every_idempotent_command_is_a_Result_returning_command(Type commandType)
    {
        Type? commandInterface = commandType.GetInterfaces()
            .SingleOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>));

        Assert.True(commandInterface is not null,
            $"{commandType.Name} implements IIdempotentCommand but is not an ICommand<>.");
        Assert.True(typeof(Result).IsAssignableFrom(commandInterface!.GetGenericArguments()[0]),
            $"{commandType.Name} must respond with Result or Result<T> so its outcome can be stored and replayed.");
        Assert.False(commandType.GetInterfaces().Any(
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQuery<>)),
            $"{commandType.Name} is a query; replaying a read is meaningless.");
    }

    /// <summary>
    /// AD-39 payload rule: an idempotent command's success payload is stored as plain JSON by
    /// StoredOutcomeCodec and replayed by deserializing it back. <see cref="Money"/> and
    /// <see cref="Currency"/> have NO JSON wire form (private construction; <c>Currency</c> throws
    /// when unset), so a Money-typed member would round-trip to <c>default</c> silently — a replay
    /// that quietly zeroes an amount. This theory turns that landmine into a build failure.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllIdempotentCommands))]
    public void Idempotent_command_payloads_never_carry_Money_or_Currency(Type commandType)
    {
        Type? commandInterface = commandType.GetInterfaces()
            .SingleOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>));
        Type? responseType = commandInterface?.GetGenericArguments()[0];

        if (responseType is null || !responseType.IsGenericType
            || responseType.GetGenericTypeDefinition() != typeof(Result<>))
        {
            return; // Plain Result — no payload to store, nothing to check.
        }

        Type payloadType = responseType.GetGenericArguments()[0];
        var offenders = new List<string>();
        CollectMoneyTypedMembers(payloadType, payloadType.Name, [], offenders);

        Assert.True(offenders.Count == 0,
            $"{commandType.Name}'s stored payload reaches Money/Currency-typed member(s): " +
            $"{string.Join(", ", offenders)}. Money and Currency have no JSON wire form (private " +
            "construction; Currency throws when unset), so StoredOutcomeCodec would replay them as " +
            "silent defaults. Put amount + currency-code primitives on the DTO instead — the " +
            "documented Money wire convention (e.g. decimal Amount + string CurrencyCode).");
    }

    /// <summary>
    /// Walks the public property graph of <paramref name="type"/> (recursively, through nullable
    /// wrappers, collection element types, and generic arguments), recording every path that ends
    /// in a <see cref="Money"/>- or <see cref="Currency"/>-typed member.
    /// </summary>
    private static void CollectMoneyTypedMembers(Type type, string path, HashSet<Type> visited, List<string> offenders)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(Money) || type == typeof(Currency))
        {
            offenders.Add(path);
            return;
        }

        // Leaves the codec serializes as JSON scalars — nothing beneath them can hide a Money.
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
            || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan) || type == typeof(DateOnly) || type == typeof(TimeOnly)
            || type == typeof(Uri) || type == typeof(object))
        {
            return;
        }

        if (!visited.Add(type))
        {
            return; // Cycle (or an already-checked type) — every path through it is covered once.
        }

        if (type.IsArray)
        {
            CollectMoneyTypedMembers(type.GetElementType()!, $"{path}[]", visited, offenders);
            return;
        }

        if (type.IsGenericType)
        {
            // Covers List<T>, IReadOnlyList<T>, Dictionary<K,V> (via K and V), KeyValuePair<K,V>, …
            foreach (Type argument in type.GetGenericArguments())
            {
                CollectMoneyTypedMembers(argument, $"{path}<{argument.Name}>", visited, offenders);
            }

            if (typeof(IEnumerable).IsAssignableFrom(type))
            {
                return; // Collection contents are the generic arguments just walked.
            }
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            CollectMoneyTypedMembers(property.PropertyType, $"{path}.{property.Name}", visited, offenders);
        }
    }
}
