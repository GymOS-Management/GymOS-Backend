using GymOS.SharedKernel;
using GymOS.SharedKernel.Results;
using GymOS.SharedKernel.Rules;
using GymOS.TestingSupport;
using Xunit;

namespace GymOS.SharedKernel.UnitTests;

/// <summary>
/// The business-rule framework, and worked examples of the shapes feature modules will use.
///
/// The examples below are deliberately generic — a range check, a required value, a composed rule.
/// No gym semantics: this milestone builds the mechanism, and the modules that own the rules define
/// what they mean.
/// </summary>
public sealed class BusinessRuleTests
{
    // ── Examples: the three shapes a module rule takes ───────────────────────────

    /// <summary>A value must be within a range. The commonest shape by far.</summary>
    private sealed record MustBeWithinRange(string Field, int Value, int Minimum, int Maximum) : IBusinessRule
    {
        public string Code => "example.out_of_range";

        // The message names the bound and the actual value: "invalid value" tells a caller nothing
        // they can act on.
        public string Message => $"{Field} must be between {Minimum} and {Maximum}, but was {Value}.";

        public bool IsBroken() => Value < Minimum || Value > Maximum;
    }

    /// <summary>A value must be present. Trivial, and worth naming so the code is stable.</summary>
    private sealed record MustBeProvided(string Field, string? Value) : IBusinessRule
    {
        public string Code => "example.required";

        public string Message => $"{Field} is required.";

        public bool IsBroken() => string.IsNullOrWhiteSpace(Value);
    }

    /// <summary>
    /// A rule composed of others — the shape for "one of these must hold", which cannot be expressed
    /// by listing rules side by side, since that means "all of these must hold".
    /// </summary>
    private sealed class AtLeastOneOf : IBusinessRule
    {
        private readonly IBusinessRule[] _alternatives;

        public AtLeastOneOf(string code, string message, params IBusinessRule[] alternatives)
        {
            Code = code;
            Message = message;
            _alternatives = alternatives;
        }

        public string Code { get; }

        public string Message { get; }

        public bool IsBroken() => _alternatives.All(rule => rule.IsBroken());
    }

    // ── Check: for aggregates, where an invalid state must not exist ─────────────

    [Fact]
    public void CheckThrowsWhenARuleIsBroken()
    {
        var rule = new MustBeWithinRange("Capacity", Value: 0, Minimum: 1, Maximum: 100);

        BusinessRuleViolationException violation =
            Assert.Throws<BusinessRuleViolationException>(() => BusinessRules.Check(rule));

        Assert.Equal("example.out_of_range", violation.Code);
        Assert.Same(rule, violation.BrokenRule);
    }

    [Fact]
    public void CheckIsSilentWhenTheRuleHolds()
    {
        BusinessRules.Check(new MustBeWithinRange("Capacity", Value: 20, Minimum: 1, Maximum: 100));
    }

    [Fact]
    public void ABrokenRuleIsADomainException()
    {
        // The whole reason BusinessRuleViolationException derives from DomainException: handlers and
        // the host already treat that as "the domain refused this", and rules must not need a second
        // strategy alongside it.
        Assert.Throws<BusinessRuleViolationException>(
            () => BusinessRules.Check(new MustBeProvided("Name", null)));

        DomainException asDomainException = Assert.Throws<BusinessRuleViolationException>(
            () => BusinessRules.Check(new MustBeProvided("Name", null)));

        Assert.IsAssignableFrom<DomainException>(asDomainException);
    }

    [Fact]
    public void TheExceptionMessageNamesTheRule()
    {
        // Exception text is what reaches a log; one that describes the failure without naming the
        // rule leaves the reader grepping for the wording.
        var violation = new BusinessRuleViolationException(new MustBeProvided("Name", null));

        Assert.Contains("example.required", violation.Message, StringComparison.Ordinal);
        Assert.Contains("Name is required.", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckAllStopsAtTheFirstBrokenRule()
    {
        var second = new MustBeProvided("Name", null);

        BusinessRuleViolationException violation = Assert.Throws<BusinessRuleViolationException>(() =>
            BusinessRules.CheckAll(
                new MustBeWithinRange("Capacity", 5, 1, 100),   // holds
                second,                                          // breaks here
                new MustBeWithinRange("Capacity", 0, 1, 100)));  // never evaluated

        Assert.Equal(second.Code, violation.Code);
    }

    // ── Validate: for handlers, where a broken rule is an answer ─────────────────

    [Fact]
    public void ValidateReportsABrokenRuleAsAValidationFailure()
    {
        Result result = BusinessRules.Validate(new MustBeProvided("Email", "   "));

        // A Result rather than an exception: the caller gets a 400 naming the rule, not a 500.
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("example.required", result.Error.Code);
        Assert.Equal("Email is required.", result.Error.Message);
    }

    [Fact]
    public void ValidateSucceedsWhenEveryRuleHolds()
    {
        Result result = BusinessRules.Validate(
            new MustBeProvided("Email", "someone@example.com"),
            new MustBeWithinRange("Capacity", 10, 1, 100));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ValidateReportsTheFirstViolationOfMany()
    {
        Result result = BusinessRules.Validate(
            new MustBeWithinRange("Capacity", 0, 1, 100),
            new MustBeProvided("Email", null));

        Assert.Equal("example.out_of_range", result.Error!.Code);
    }

    [Fact]
    public void TheSameRuleObjectServesBothPaths()
    {
        // One definition, two ways of reporting it — which is what stops a rule enforced in the
        // domain from drifting away from the message the API returns for it.
        var rule = new MustBeWithinRange("Capacity", Value: 0, Minimum: 1, Maximum: 100);

        Result asResult = BusinessRules.Validate(rule);
        BusinessRuleViolationException asException =
            Assert.Throws<BusinessRuleViolationException>(() => BusinessRules.Check(rule));

        Assert.Equal(rule.Code, asResult.Error!.Code);
        Assert.Equal(rule.Code, asException.Code);
        Assert.Equal(asResult.Error.Message, rule.Message);
    }

    [Fact]
    public void IsSatisfiedAnswersWithoutThrowingOrAllocating()
    {
        Assert.True(BusinessRules.IsSatisfied(new MustBeProvided("Name", "Ada")));
        Assert.False(BusinessRules.IsSatisfied(new MustBeProvided("Name", null)));
    }

    // ── Composition ─────────────────────────────────────────────────────────────

    [Fact]
    public void RulesComposeIntoEitherOrRequirements()
    {
        var rule = new AtLeastOneOf(
            "example.contact_required",
            "Either an email address or a phone number is required.",
            new MustBeProvided("Email", null),
            new MustBeProvided("Phone", "+357 99 000000"));

        // Listing rules side by side means "all must hold"; this is the shape for "one must".
        BusinessRuleAssert.Satisfied(rule);
        BusinessRuleAssert.Broken(
            new AtLeastOneOf(
                "example.contact_required",
                "Either an email address or a phone number is required.",
                new MustBeProvided("Email", null),
                new MustBeProvided("Phone", null)),
            "example.contact_required");
    }

    [Fact]
    public void RulesAreValueObjectsSoTheyCompareByTheirInputs()
    {
        // Records, so two rules over the same values are equal — which makes them easy to build in a
        // loop, deduplicate, and assert on.
        Assert.Equal(
            new MustBeWithinRange("Capacity", 0, 1, 100),
            new MustBeWithinRange("Capacity", 0, 1, 100));
    }

    // ── Testing support ─────────────────────────────────────────────────────────

    [Fact]
    public void TheAssertionHelperFailsWhenARuleDoesNotBehaveAsExpected()
    {
        Assert.Throws<BusinessRuleAssertionException>(
            () => BusinessRuleAssert.Broken(new MustBeProvided("Name", "Ada")));

        Assert.Throws<BusinessRuleAssertionException>(
            () => BusinessRuleAssert.Satisfied(new MustBeProvided("Name", null)));

        Assert.Throws<BusinessRuleAssertionException>(
            () => BusinessRuleAssert.Broken(new MustBeProvided("Name", null), "wrong.code"));
    }

    [Fact]
    public void RuleCodesFollowTheDottedLowerCaseConvention()
    {
        // Codes are API: clients branch on them, so their shape is a contract and worth asserting
        // over a module's whole catalog at once.
        BusinessRuleAssert.CodesFollowConvention(
            new MustBeProvided("Name", null),
            new MustBeWithinRange("Capacity", 0, 1, 100));
    }

    [Fact]
    public void TheConventionCheckRejectsAMalformedCode()
    {
        Assert.Throws<BusinessRuleAssertionException>(
            () => BusinessRuleAssert.CodesFollowConvention(new BadlyNamedRule()));
    }

    private sealed class BadlyNamedRule : IBusinessRule
    {
        public string Code => "NotDotted";

        public string Message => "Whatever.";

        public bool IsBroken() => true;
    }
}
