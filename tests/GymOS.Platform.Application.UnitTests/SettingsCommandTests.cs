using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.DependencyInjection;
using GymOS.Platform.Application.Settings;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Application.UnitTests.Fakes;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Settings;
using GymOS.SharedKernel.Results;
using GymOS.TestingSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// The write pipeline, exercised through the real dispatcher and the real authorization
/// behavior — a caller without the right permission never reaches <see cref="SettingsWriter"/>.
/// </summary>
public sealed class SettingsCommandTests
{
    private sealed class Fixture
    {
        private readonly ServiceProvider _provider;

        public Fixture()
        {
            Provider = new FakeSettingsProvider(Repository, Protector);

            var services = new ServiceCollection();

            services.AddCqrsDispatcher();
            services.AddAuthorizationBehavior();
            services.AddPlatformApplication();

            services.AddSingleton<ISettingsRepository>(Repository);
            services.AddSingleton<ISettingsProvider>(Provider);
            services.AddSingleton<ISettingProtector>(Protector);
            services.AddSingleton<ISettingsValidator>(Validator);
            services.AddSingleton<IUnitOfWork>(UnitOfWork);

            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<ICurrentUser>(CurrentUser);
            services.AddSingleton<IPermissionEvaluator>(Permissions);
            services.AddSingleton<IAuditTrail>(AuditTrail);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

            _provider = services.BuildServiceProvider();
        }

        public FakeSettingsRepository Repository { get; } = new();

        public FakeSettingProtector Protector { get; } = new();

        public FakeSettingsProvider Provider { get; }

        public FakeSettingsValidator Validator { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

        public FakeCurrentUser CurrentUser { get; } = new();

        public FakePermissionEvaluator Permissions { get; } = new();

        public FakeAuditTrail AuditTrail { get; } = new();

        public IDispatcher Dispatcher => _provider.GetRequiredService<IDispatcher>();

        public Fixture ActingAs(params string[] permissions)
        {
            CurrentUser.UserId = Guid.Parse("99999999-9999-9999-9999-999999999999");
            CurrentUser.Email = "operator@gymos.test";

            foreach (string permission in permissions)
            {
                Permissions.Granted.Add(permission);
            }

            return this;
        }

        public PlatformSetting SeedRow(string key, string? plainValue, string? protectedValue, Guid? updatedBy = null)
        {
            if (!PlatformSettingDefinitions.TryGet(key, out SettingDefinition definition))
            {
                throw new InvalidOperationException($"No definition named '{key}'.");
            }

            PlatformSetting row = PlatformSetting.Create(Guid.NewGuid(), definition, plainValue, protectedValue, updatedBy);
            Repository.Add(row);
            return row;
        }
    }

    [Fact]
    public async Task AnUnknownKeyIsRefusedAndNothingIsWritten()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.SettingsManage);

        Result<IReadOnlyList<SettingView>> result = await fixture.Dispatcher.SendAsync(
            new UpdateSettingsCommand(SettingCategory.Email, [new SettingValueWrite("email.nope", "x")]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("settings.unknown_key", result.Error!.Code);
        Assert.Empty(fixture.Repository.Rows);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task AKeyFromAnotherCategoryIsRefused()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.SettingsManage);

        Result<IReadOnlyList<SettingView>> result = await fixture.Dispatcher.SendAsync(
            new UpdateSettingsCommand(SettingCategory.Email, [new SettingValueWrite("security.lockout_minutes", "10")]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("settings.wrong_category", result.Error!.Code);
        Assert.Empty(fixture.Repository.Rows);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task ABadValueNamesTheKeyAndTheRule()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.SettingsManage);

        Result<IReadOnlyList<SettingView>> result = await fixture.Dispatcher.SendAsync(
            new UpdateSettingsCommand(SettingCategory.Email, [new SettingValueWrite("email.smtp_port", "70000")]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("settings.invalid_value", result.Error!.Code);
        Assert.Contains("email.smtp_port", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("between 1 and 65535", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMaskLeavesASensitiveRowUntouched()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.SettingsManage);
        Guid seededUpdatedBy = Guid.NewGuid();
        string protectedValue = fixture.Protector.Protect("email.smtp_password", "hunter2");
        fixture.SeedRow("email.smtp_password", null, protectedValue, seededUpdatedBy);

        Result<IReadOnlyList<SettingView>> result = await fixture.Dispatcher.SendAsync(
            new UpdateSettingsCommand(SettingCategory.Email, [new SettingValueWrite("email.smtp_password", PlatformSettingDefinitions.Mask)]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        PlatformSetting stored = Assert.Single(fixture.Repository.Rows);
        Assert.Equal(protectedValue, stored.ProtectedValue);
        Assert.Equal(seededUpdatedBy, stored.UpdatedBy);
    }

    [Fact]
    public async Task AnEmptyStringOrNullDeletesTheRow()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.SettingsManage);
        fixture.SeedRow("email.smtp_host", "smtp.example.com", null);

        Result<IReadOnlyList<SettingView>> emptyResult = await fixture.Dispatcher.SendAsync(
            new UpdateSettingsCommand(SettingCategory.Email, [new SettingValueWrite("email.smtp_host", "")]),
            CancellationToken.None);

        Assert.True(emptyResult.IsSuccess);
        Assert.Empty(fixture.Repository.Rows);

        fixture.SeedRow("email.smtp_host", "smtp.example.com", null);

        Result<IReadOnlyList<SettingView>> nullResult = await fixture.Dispatcher.SendAsync(
            new UpdateSettingsCommand(SettingCategory.Email, [new SettingValueWrite("email.smtp_host", null)]),
            CancellationToken.None);

        Assert.True(nullResult.IsSuccess);
        Assert.Empty(fixture.Repository.Rows);
    }

    [Fact]
    public async Task ANewSensitiveValueIsProtectedBeforeItIsStored()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.SettingsManage);

        Result<IReadOnlyList<SettingView>> result = await fixture.Dispatcher.SendAsync(
            new UpdateSettingsCommand(SettingCategory.Email, [new SettingValueWrite("email.smtp_password", "hunter2")]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        PlatformSetting stored = Assert.Single(fixture.Repository.Rows);
        Assert.Null(stored.Value);
        Assert.Equal("enc(email.smtp_password:hunter2)", stored.ProtectedValue);
    }

    [Fact]
    public async Task TheValidatorSeesTheWouldBeOverlayAndCanRejectTheBatch()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.SettingsManage);
        fixture.Validator.RejectWith = "Smtp:Host required";

        Result<IReadOnlyList<SettingView>> result = await fixture.Dispatcher.SendAsync(
            new UpdateSettingsCommand(SettingCategory.Email, [new SettingValueWrite("email.enabled", "true")]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("settings.invalid_state", result.Error!.Code);
        Assert.Equal("Smtp:Host required", result.Error.Message);
        Assert.Empty(fixture.Repository.Rows);
        Assert.Equal("true", fixture.Validator.LastOverlay!["Email:Enabled"]);
    }

    [Fact]
    public async Task TheMaintenanceCategoryIsRefusedHere()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.SettingsManage);

        Result<IReadOnlyList<SettingView>> result = await fixture.Dispatcher.SendAsync(
            new UpdateSettingsCommand(SettingCategory.Maintenance, [new SettingValueWrite("maintenance.enabled", "true")]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("settings.use_maintenance_endpoint", result.Error!.Code);
    }

    [Fact]
    public async Task ASuccessfulWriteReturnsTheCategoryMaskedAndInvalidatesTheCache()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.SettingsManage);

        Result<IReadOnlyList<SettingView>> result = await fixture.Dispatcher.SendAsync(
            new UpdateSettingsCommand(SettingCategory.Email,
            [
                new SettingValueWrite("email.smtp_host", "smtp.example.com"),
                new SettingValueWrite("email.smtp_password", "hunter2"),
            ]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        SettingView host = result.Value.Single(v => v.Key == "email.smtp_host");
        SettingView password = result.Value.Single(v => v.Key == "email.smtp_password");
        Assert.Equal("smtp.example.com", host.Value);
        Assert.Equal(SettingSource.Database, host.Source);
        Assert.Equal(PlatformSettingDefinitions.Mask, password.Value);
        Assert.Equal(SettingSource.Database, password.Source);
        Assert.Equal(1, fixture.Provider.Invalidations);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task GetReturnsEveryDefinitionInTheCategoryWithItsSource()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.SettingsRead);

        Result<IReadOnlyList<SettingView>> result = await fixture.Dispatcher.QueryAsync(
            new GetSettingsQuery(SettingCategory.Email), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Value.Count);
        SettingView port = result.Value.Single(v => v.Key == "email.smtp_port");
        Assert.Equal(SettingSource.Default, port.Source);
        Assert.Equal("1025", port.Value);
    }

    [Fact]
    public async Task ReadNeedsSettingsReadAndWriteNeedsSettingsManage()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.SettingsRead);

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            fixture.Dispatcher.SendAsync(
                new UpdateSettingsCommand(SettingCategory.Email, [new SettingValueWrite("email.smtp_host", "smtp.example.com")]),
                CancellationToken.None));
    }
}
