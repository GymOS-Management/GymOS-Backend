using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Application.Settings;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Settings;
using GymOS.Platform.Infrastructure.Email;
using GymOS.Platform.Infrastructure.Identity;
using GymOS.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace GymOS.Platform.Infrastructure.Settings;

/// <summary>
/// Cross-field validation for a category's write (M7 spec §4): binds the category's options class
/// (or classes) against the proposed overlay through the same <see cref="SettingsBinder{T}"/> the
/// running application reads from, so "would this write be accepted" and "how is a row currently
/// interpreted" never disagree.
/// </summary>
internal sealed class OptionsSettingsValidator : ISettingsValidator
{
    private readonly IServiceProvider _services;

    public OptionsSettingsValidator(IServiceProvider services) => _services = services;

    public Task<Result> ValidateAsync(SettingCategory category, IReadOnlyDictionary<string, string?> overlay, CancellationToken cancellationToken)
    {
        string? failures = category switch
        {
            SettingCategory.General or SettingCategory.Email => Check<EmailOptions>(overlay),
            SettingCategory.Security => Check<LoginPolicyOptions>(overlay) ?? Check<JwtOptions>(overlay) ?? Check<RateLimitOptions>(overlay),
            SettingCategory.Maintenance => Check<MaintenanceOptions>(overlay),
            SettingCategory.Retention => Check<MonitoringOptions>(overlay),
            _ => null,
        };

        return Task.FromResult(failures is null ? Result.Success() : Result.Failure(SettingErrors.InvalidState(failures)));
    }

    private string? Check<T>(IReadOnlyDictionary<string, string?> overlay) where T : class, new()
    {
        var binder = _services.GetRequiredService<ISettings<T>>() as SettingsBinder<T>
            ?? throw new InvalidOperationException(
                $"ISettings<{typeof(T).Name}> is not the platform binder; AddBoundSettings<T> must register it.");

        return binder.Bind(overlay).Failures;
    }
}
