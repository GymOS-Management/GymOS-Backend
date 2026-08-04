using GymOS.Platform.Application.Identity;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

public sealed class PasswordPolicyTests
{
    private const int MinimumLength = 12;

    [Fact]
    public void APasswordAtTheMinimumLength_IsAccepted()
    {
        Result result = PasswordPolicy.Validate(new string('a', MinimumLength), MinimumLength);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void OneCharacterShort_IsRejected()
    {
        Result result = PasswordPolicy.Validate(new string('a', MinimumLength - 1), MinimumLength);

        Assert.Equal("auth.weak_password", result.Error!.Code);
        Assert.Contains(MinimumLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("        ")]
    public void MissingOrBlank_IsRejected(string? password)
    {
        Result result = PasswordPolicy.Validate(password, MinimumLength);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
    }

    [Theory]
    [InlineData(" leading-whitespace-pw")]
    [InlineData("trailing-whitespace-pw ")]
    public void SurroundingWhitespace_IsRejectedRatherThanSilentlyTrimmed(string password)
    {
        // Trimming would let a copy-paste succeed here and then fail at the next
        // sign-in with no explanation.
        Result result = PasswordPolicy.Validate(password, MinimumLength);

        Assert.Equal("auth.weak_password", result.Error!.Code);
    }

    [Fact]
    public void InternalSpaces_AreAllowed_BecausePassphrasesAreGood()
    {
        Result result = PasswordPolicy.Validate("correct horse battery staple", MinimumLength);

        Assert.True(result.IsSuccess);
    }
}
