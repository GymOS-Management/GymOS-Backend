using System.Reflection;
using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.ArchitectureTests;

/// <summary>
/// Rule R4 — "no naked <see cref="decimal"/> money arithmetic; use <see cref="Money"/>"
/// (ARCHITECTURE-ADDENDUM.md §8, AD-42). This is the rule's first enforcer: until <c>Money</c> existed
/// it had no subject.
///
/// <para>Reflection over members rather than NetArchTest, because the rule is about the *shape* of a
/// signature — a <see cref="decimal"/> named like money — and not about a dependency between types.
/// The heuristic is names, as the spec says it is: nothing can prove a <see cref="decimal"/> is money,
/// but a member called <c>Price</c> or <c>TotalAmount</c> declares its intent, and the cost of a false
/// positive is one entry in an exemption list.</para>
///
/// <para><b>What this catches.</b> The failure it exists for is a plausible-looking
/// <c>decimal Total { get; }</c> on an invoice, summed with <c>+</c>, rounded by whichever call site
/// got there first, and quietly adding a currency-less number to a figure in another currency. By the
/// time that is visible it is in a customer's statement.</para>
/// </summary>
public sealed class MoneyTypeTests
{
    /// <summary>
    /// Words that mean "this is an amount of money". Matched case-insensitively against member names.
    /// </summary>
    private static readonly string[] MoneyWords =
    [
        "price", "amount", "total", "subtotal", "cost", "fee", "balance", "credit", "debit",
        "charge", "refund", "discount", "tax", "vat", "revenue", "mrr", "payment", "salary",
        "wage", "money", "cash", "deposit", "invoice",
    ];

    /// <summary>
    /// Members whose name matches a money word but which are genuinely not money, each with its
    /// reason. Adding an entry is where the argument happens — the same pattern as the anonymous
    /// endpoint list.
    /// </summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal)
    {
        // Money's own surface: it is the type the rule points at, so its decimal is the sanctioned one.
        "GymOS.SharedKernel.Currencies.Money.Amount",
        "GymOS.SharedKernel.Currencies.Money.Of",
        "GymOS.SharedKernel.Currencies.Money.FromMinorUnits",
        "GymOS.SharedKernel.Currencies.Money.ToMinorUnits",
    };

    /// <summary>
    /// Whether a money-shaped <see cref="decimal"/> is acceptable because it sits at a serialisation
    /// boundary, beside the currency it is denominated in.
    ///
    /// <para><b>Why this is a rule rather than a growing exemption list.</b> R4's purpose is "never an
    /// amount without its unit", and <c>Money</c> exists to make that structural. But <c>Money</c> has no
    /// wire form — <c>Currency</c> keeps its code in a private field behind a property that throws when
    /// unset, so System.Text.Json would quietly produce a default instead of failing — so an event payload
    /// or a JSON DTO genuinely cannot hold one. Those carry an amount and an ISO code side by side and
    /// rebuild a <c>Money</c> on the way in. Listing each such member by name meant four more entries per
    /// money-touching increment, all making the same argument; encoding the argument once is better.</para>
    ///
    /// <para>Two conditions, both needed. The type must declare a currency member — that is the actual
    /// principle, and a lone <c>decimal Total</c> still fails. And it must be a <c>record</c>, which in
    /// this codebase is how DTOs, commands, and events are written while aggregates are classes. The
    /// second is a **proxy** for "is a serialisation boundary" rather than a proof of it: a domain value
    /// object written as a record could pair a decimal with a currency string and slip through. That is
    /// the residual risk, and it is smaller than a list nobody reads.</para>
    /// </summary>
    private static bool TravelsWithItsCurrency(Type declaringType)
    {
        // `PrintMembers` is compiler-generated for every record and record struct, and for nothing else —
        // a more reliable marker than Deconstruct (positional records only) or EqualityContract
        // (record classes only).
        bool isRecord = declaringType.GetMethod(
            "PrintMembers",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null;

        if (!isRecord)
        {
            return false;
        }

        return declaringType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.Name.Contains("Currency", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoProductionMemberExposesMoneyAsANakedDecimal()
    {
        var violations = new List<string>();

        foreach (Assembly assembly in ProductionAssemblies.All)
        {
            foreach (Type type in assembly.GetTypes().Where(type => !IsCompilerGenerated(type)))
            {
                violations.AddRange(MoneyShapedMembers(type));
            }
        }

        Assert.True(
            violations.Count == 0,
            "Members exposing money as a decimal (rule R4 — use Money, or add the member to the " +
            $"exemption list with a reason):{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", violations));
    }

    [Fact]
    public void TheExemptionListContainsOnlyMembersThatStillExist()
    {
        // A stale exemption is worse than none: it silently excuses whatever member later takes that
        // name.
        var present = ProductionAssemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(member => $"{type.FullName}.{member.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        var stale = Exempt.Where(entry => !present.Contains(entry)).ToList();

        Assert.True(stale.Count == 0, "Exempted members that no longer exist: " + string.Join(", ", stale));
    }

    [Fact]
    public void MoneyIsImmutable()
    {
        // An amount that can be mutated in place is an amount two callers can disagree about, and every
        // guarantee above — rounding on construction, currency checking — assumes it cannot happen.
        Assert.True(typeof(Money).IsValueType);

        Assert.All(
            typeof(Money).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.False(property.CanWrite, $"Money.{property.Name} has a setter."));

        Assert.All(
            typeof(Currency).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.False(property.CanWrite, $"Currency.{property.Name} has a setter."));
    }

    private static IEnumerable<string> MoneyShapedMembers(Type type)
    {
        foreach (PropertyInfo property in type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (IsMoneyShaped(property.PropertyType) && LooksLikeMoney(property.Name)
                && !Exempt.Contains($"{type.FullName}.{property.Name}")
                && !TravelsWithItsCurrency(type))
            {
                yield return $"{type.FullName}.{property.Name} : {Describe(property.PropertyType)}";
            }
        }

        foreach (MethodInfo method in type.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            // Property accessors are reported through the property itself; listing get_Total() beside
            // Total would double every finding.
            if (method.IsSpecialName
                || Exempt.Contains($"{type.FullName}.{method.Name}")
                || IsCompilerGenerated(method))
            {
                continue;
            }

            if (IsMoneyShaped(method.ReturnType) && LooksLikeMoney(method.Name)
                && !TravelsWithItsCurrency(type))
            {
                yield return $"{type.FullName}.{method.Name}() returns {Describe(method.ReturnType)}";
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (IsMoneyShaped(parameter.ParameterType)
                    && LooksLikeMoney(parameter.Name ?? string.Empty)
                    && !TravelsWithItsCurrency(type))
                {
                    yield return
                        $"{type.FullName}.{method.Name}(… {Describe(parameter.ParameterType)} {parameter.Name})";
                }
            }
        }
    }

    /// <summary>A decimal, or a collection or nullable of one.</summary>
    private static bool IsMoneyShaped(Type type)
    {
        if (type == typeof(decimal) || type == typeof(decimal?))
        {
            return true;
        }

        if (type.IsArray)
        {
            return IsMoneyShaped(type.GetElementType()!);
        }

        return type.IsGenericType
            && type.GetGenericArguments() is [Type single]
            && single == typeof(decimal);
    }

    private static bool LooksLikeMoney(string name) =>
        MoneyWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string Describe(Type type) => type == typeof(decimal) ? "decimal" : type.Name;

    private static bool IsCompilerGenerated(MemberInfo member) =>
        member.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);
}
