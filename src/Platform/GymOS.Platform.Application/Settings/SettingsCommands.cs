using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Settings;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Settings;

/// <summary>Writes one category's settings batch (M7 spec §4). Refuses the <see
/// cref="SettingCategory.Maintenance"/> category outright — that one changes only through the
/// confirmed maintenance-mode endpoint (Task 9), never through this general-purpose write.</summary>
[RequiresPermission(PlatformPermissions.SettingsManage)]
public sealed record UpdateSettingsCommand(SettingCategory Category, IReadOnlyList<SettingValueWrite> Values) : ICommand<Result<IReadOnlyList<SettingView>>>;

// Task 8's SMTP-test command and Task 9's set-maintenance-mode command join this file.

internal sealed class UpdateSettingsCommandHandler : ICommandHandler<UpdateSettingsCommand, Result<IReadOnlyList<SettingView>>>
{
    private readonly SettingsWriter _writer;

    public UpdateSettingsCommandHandler(SettingsWriter writer) => _writer = writer;

    public Task<Result<IReadOnlyList<SettingView>>> HandleAsync(UpdateSettingsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Category == SettingCategory.Maintenance
            ? Task.FromResult(Result.Failure<IReadOnlyList<SettingView>>(SettingErrors.UseMaintenanceEndpoint))
            : _writer.ApplyAsync(command.Category, command.Values, cancellationToken);
    }
}
