using System.Globalization;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Auditing;

[RequiresPermission(PlatformPermissions.AuditExport)]
public sealed record ExportAuditLogsQuery(
    Guid? UserId,
    string? Action,
    string? EntityType,
    Guid? EntityId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc) : IQuery<Result<AuditLogExport>>;

/// <summary>
/// The normalised filter alongside the (unenumerated) rows it matches. The handler validates and
/// builds the filter but never enumerates <see cref="Rows"/> itself — the endpoint streams them to
/// the response body and records the real count once enumeration finishes (M7 spec §8).
/// </summary>
public sealed record AuditLogExport(AuditLogExportFilter Filter, IAsyncEnumerable<AuditLogListItem> Rows);

internal sealed class ExportAuditLogsQueryHandler : IQueryHandler<ExportAuditLogsQuery, Result<AuditLogExport>>
{
    private readonly IAuditLogQueries _auditLogQueries;

    public ExportAuditLogsQueryHandler(IAuditLogQueries auditLogQueries) => _auditLogQueries = auditLogQueries;

    public Task<Result<AuditLogExport>> HandleAsync(
        ExportAuditLogsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.FromUtc is not { } from || query.ToUtc is not { } to)
        {
            return Task.FromResult(Result.Failure<AuditLogExport>(
                Error.Validation("audit.export_range", "`from` and `to` are required.")));
        }

        if (from > to)
        {
            return Task.FromResult(Result.Failure<AuditLogExport>(
                Error.Validation("audit.export_range", "`from` must not be later than `to`.")));
        }

        if (to - from > TimeSpan.FromDays(AuditCsv.MaxRangeDays))
        {
            return Task.FromResult(Result.Failure<AuditLogExport>(
                Error.Validation(
                    "audit.export_range",
                    $"An audit export covers at most {AuditCsv.MaxRangeDays} days. Ask for a shorter period.")));
        }

        var filter = new AuditLogExportFilter(query.UserId, query.Action, query.EntityType, query.EntityId, from, to);

        return Task.FromResult(Result.Success(
            new AuditLogExport(filter, _auditLogQueries.StreamAsync(filter, cancellationToken))));
    }
}

/// <summary>
/// Records that an export happened (M7 spec §8) — dispatched by the endpoint after the stream has
/// finished, so <see cref="RowCount"/> is the number of rows actually written rather than an estimate
/// taken before the body was sent.
/// </summary>
[RequiresPermission(PlatformPermissions.AuditExport)]
public sealed record RecordAuditExportCommand(AuditLogExportFilter Filter, long RowCount) : ICommand<Result>;

internal sealed class RecordAuditExportCommandHandler : ICommandHandler<RecordAuditExportCommand, Result>
{
    private readonly IAuditTrail _auditTrail;
    private readonly IUnitOfWork _unitOfWork;

    public RecordAuditExportCommandHandler(IAuditTrail auditTrail, IUnitOfWork unitOfWork)
    {
        _auditTrail = auditTrail;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> HandleAsync(RecordAuditExportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        AuditLogExportFilter filter = command.Filter;

        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["userId"] = filter.UserId?.ToString("D", CultureInfo.InvariantCulture),
            ["action"] = filter.Action,
            ["entityType"] = filter.EntityType,
            ["entityId"] = filter.EntityId?.ToString("D", CultureInfo.InvariantCulture),
            ["fromUtc"] = filter.FromUtc.ToString("o", CultureInfo.InvariantCulture),
            ["toUtc"] = filter.ToUtc.ToString("o", CultureInfo.InvariantCulture),
            ["rowCount"] = command.RowCount.ToString(CultureInfo.InvariantCulture),
        };

        _auditTrail.Record("audit.exported", details: details);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
