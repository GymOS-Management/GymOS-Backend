using GymOS.BuildingBlocks.Application.Abstractions;
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

// Task 9's set-maintenance-mode command joins this file.

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

/// <summary>Tests the effective SMTP settings without sending mail (M7 spec §4, Task 8). ADR-53
/// permits <see cref="IEmailSender"/> exactly two messages, and this command never resolves that
/// interface: it connects, authenticates, and issues a NOOP through <see cref="ISmtpConnectionTester"/>,
/// which composes no message and never touches the sender.</summary>
[RequiresPermission(PlatformPermissions.SettingsManage)]
public sealed record TestEmailConnectionCommand : ICommand<Result<SmtpConnectionTestResult>>;

internal sealed class TestEmailConnectionCommandHandler
    : ICommandHandler<TestEmailConnectionCommand, Result<SmtpConnectionTestResult>>
{
    private readonly ISmtpConnectionTester _tester;
    private readonly IAuditTrail _auditTrail;
    private readonly IUnitOfWork _unitOfWork;

    public TestEmailConnectionCommandHandler(ISmtpConnectionTester tester, IAuditTrail auditTrail, IUnitOfWork unitOfWork)
    {
        _tester = tester;
        _auditTrail = auditTrail;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<SmtpConnectionTestResult>> HandleAsync(
        TestEmailConnectionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_tester.IsEmailEnabled)
        {
            return Result.Failure<SmtpConnectionTestResult>(SettingErrors.EmailDisabled);
        }

        SmtpConnectionTestResult result = await _tester.TestAsync(cancellationToken);

        _auditTrail.Record(
            "settings.email_tested",
            details: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ok"] = result.Ok.ToString(),
                ["stage"] = result.Stage,
                ["error"] = result.Error,
            });

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(result);
    }
}
