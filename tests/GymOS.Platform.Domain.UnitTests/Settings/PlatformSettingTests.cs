using GymOS.Platform.Domain.Settings;
using GymOS.SharedKernel;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests.Settings;

public sealed class PlatformSettingTests
{
    private static SettingDefinition Plain => PlatformSettingDefinitions.All.Single(d => d.Key == "email.smtp_host");
    private static SettingDefinition Secret => PlatformSettingDefinitions.All.Single(d => d.Key == "email.smtp_password");

    [Fact]
    public void APlainSettingHoldsItsValueInTheClear()
    {
        PlatformSetting setting = PlatformSetting.Create(Guid.NewGuid(), Plain, "smtp.example.test", null, null);
        Assert.Equal("smtp.example.test", setting.Value);
        Assert.Null(setting.ProtectedValue);
        Assert.False(setting.IsSensitive);
    }

    [Fact]
    public void ASensitiveSettingHoldsOnlyCiphertext()
    {
        PlatformSetting setting = PlatformSetting.Create(Guid.NewGuid(), Secret, null, "CfDJ8...", null);
        Assert.Null(setting.Value);
        Assert.Equal("CfDJ8...", setting.ProtectedValue);
        Assert.True(setting.IsSensitive);
    }

    [Fact]
    public void APlainValueOnASensitiveDefinitionIsRefused() =>
        Assert.Throws<DomainException>(() => PlatformSetting.Create(Guid.NewGuid(), Secret, "plaintext", null, null));

    [Fact]
    public void CiphertextOnAPlainDefinitionIsRefused() =>
        Assert.Throws<DomainException>(() => PlatformSetting.Create(Guid.NewGuid(), Plain, null, "CfDJ8...", null));

    [Fact]
    public void ExactlyOneColumnMustBePopulated()
    {
        Assert.Throws<DomainException>(() => PlatformSetting.Create(Guid.NewGuid(), Plain, null, null, null));
        Assert.Throws<DomainException>(() => PlatformSetting.Create(Guid.NewGuid(), Plain, "a", "b", null));
    }

    [Fact]
    public void SetReplacesTheValueAndRecordsWho()
    {
        Guid editor = Guid.NewGuid();
        PlatformSetting setting = PlatformSetting.Create(Guid.NewGuid(), Plain, "old", null, null);
        setting.Set("new", null, editor);
        Assert.Equal("new", setting.Value);
        Assert.Equal(editor, setting.UpdatedBy);
    }
}
