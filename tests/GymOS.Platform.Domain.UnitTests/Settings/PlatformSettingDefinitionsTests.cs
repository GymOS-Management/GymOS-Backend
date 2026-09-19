using GymOS.Platform.Domain.Settings;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests.Settings;

public sealed class PlatformSettingDefinitionsTests
{
    [Fact]
    public void EveryKeyIsCategoryDotNameAndTheCategoryMatchesThePrefix()
    {
        foreach (SettingDefinition definition in PlatformSettingDefinitions.All)
        {
            string[] parts = definition.Key.Split('.');
            Assert.Equal(2, parts.Length);
            Assert.Equal(definition.Category.ToString().ToLowerInvariant(), parts[0]);
            Assert.Matches("^[a-z][a-z0-9_]*$", parts[1]);
        }
    }

    [Fact]
    public void KeysAreUnique() =>
        Assert.Equal(PlatformSettingDefinitions.All.Count, PlatformSettingDefinitions.All.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());

    [Fact]
    public void EveryDefaultIsValidForItsOwnDefinition()
    {
        foreach (SettingDefinition definition in PlatformSettingDefinitions.All.Where(d => d.Default is not null))
        {
            Assert.Null(definition.ValidateValue(definition.Default!));
        }
    }

    [Fact]
    public void TheCatalogHoldsTheSpecsTwentyFourKeys()
    {
        Assert.Equal(24, PlatformSettingDefinitions.All.Count);
        Assert.True(PlatformSettingDefinitions.TryGet("email.smtp_password", out SettingDefinition password));
        Assert.True(password.IsSensitive);
        Assert.Equal("Email:Smtp:Password", password.ConfigurationPath);
    }

    [Theory]
    [InlineData("email.smtp_port", "0", "must be between 1 and 65535")]
    [InlineData("email.smtp_port", "abc", "must be an integer")]
    [InlineData("email.enabled", "yes", "must be true or false")]
    [InlineData("security.max_failed_attempts", "-1", "must be between 1 and 100")]
    public void ValuesAreCheckedAgainstTypeAndRange(string key, string value, string expectedRule)
    {
        Assert.True(PlatformSettingDefinitions.TryGet(key, out SettingDefinition definition));
        Assert.Equal(expectedRule, definition.ValidateValue(value));
    }

    [Fact]
    public void ADurationOverlaysAsATimeSpanString()
    {
        Assert.True(PlatformSettingDefinitions.TryGet("security.lockout_minutes", out SettingDefinition lockout));
        Assert.Equal("00:15:00", lockout.ToConfigurationValue("15"));
        Assert.Equal("15", lockout.FromConfigurationValue("00:15:00"));
        Assert.True(PlatformSettingDefinitions.TryGet("security.refresh_token_days", out SettingDefinition refresh));
        Assert.Equal("7.00:00:00", refresh.ToConfigurationValue("7"));
        Assert.Equal("7", refresh.FromConfigurationValue("7.00:00:00"));
    }

    [Fact]
    public void ForCategoryReturnsOnlyThatCategory() =>
        Assert.All(PlatformSettingDefinitions.ForCategory(SettingCategory.Email), d => Assert.Equal(SettingCategory.Email, d.Category));
}
