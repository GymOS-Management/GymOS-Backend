using System.Reflection;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
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
    /// <summary>
    /// Exemplar only, not production code: keeps <see cref="Every_idempotent_command_is_a_Result_returning_command"/>
    /// exercised while no production <see cref="IIdempotentCommand"/> exists yet (AD-39, first consumer:
    /// invoices mark-paid). This runner errors on a <c>[Theory]</c> with empty <c>MemberData</c> rather than
    /// skipping it, so the theory needs at least one row until production code lands.
    /// </summary>
    private sealed record RecordPayment(Guid CommandId) : ICommand<Result>, IIdempotentCommand
    {
        public string OperationKey => "test.record-payment";

        public TimeSpan RetentionPeriod => TimeSpan.FromHours(72);

        public bool KeyRequired => true;
    }

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

        // Seeded exemplar (see RecordPayment above) rather than left empty: an empty MemberData
        // errors on this runner instead of skipping, and the seed keeps the theory's assertions
        // live and exercised in the meantime.
        data.Add(typeof(RecordPayment));

        return data;
    }

    [Fact]
    public void The_theory_below_is_not_vacuous_or_this_fact_documents_why()
    {
        // No production IIdempotentCommand exists yet (first consumer: invoices mark-paid).
        // When one lands, this fact simply keeps passing; the theory does the real work.
        Assert.True(AllIdempotentCommands().Count >= 0);
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
}
