using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Invoices.Rules;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Results;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Application.Invoices;

/// <summary>
/// Records that an invoice was paid.
///
/// <para>The platform's first production <see cref="IIdempotentCommand"/> (AD-39): marking an invoice
/// paid moves money, so a retried request must replay the stored outcome rather than record a second
/// payment. The pipeline handles the replay; the handler below stays oblivious.</para>
/// </summary>
[RequiresPermission(PlatformPermissions.BillingManage)]
public sealed record MarkInvoicePaidCommand(Guid InvoiceId, string? PaymentReference, string? ExpectedRowVersion)
    : ICommand<Result<InvoiceDetail>>, IIdempotentCommand
{
    public string OperationKey => "billing.mark-invoice-paid";

    /// <summary>Financial, so 72h — AD-39's own example of an operation worth a long memory.</summary>
    public TimeSpan RetentionPeriod => TimeSpan.FromHours(72);

    public bool KeyRequired => true;
}

/// <summary>
/// Voids an unpaid invoice. Confirmation-gated like gym deletion (M2's precedent): the caller must
/// echo the invoice number, because "void" is the kind of button a slip reaches for.
/// </summary>
[RequiresPermission(PlatformPermissions.BillingManage)]
public sealed record VoidInvoiceCommand(Guid InvoiceId, string? ConfirmAction, string? ExpectedRowVersion)
    : ICommand<Result<InvoiceDetail>>;

[RequiresPermission(PlatformPermissions.BillingRead)]
public sealed record ListInvoicesQuery(InvoiceListFilter Filter)
    : IQuery<Result<PagedResult<InvoiceListItem>>>;

[RequiresPermission(PlatformPermissions.BillingRead)]
public sealed record GetInvoiceQuery(Guid InvoiceId) : IQuery<Result<InvoiceDetail>>;

[RequiresPermission(PlatformPermissions.BillingRead)]
public sealed record ListGymInvoicesQuery(Guid GymId)
    : IQuery<Result<IReadOnlyList<InvoiceListItem>>>;

internal sealed class MarkInvoicePaidCommandHandler
    : ICommandHandler<MarkInvoicePaidCommand, Result<InvoiceDetail>>
{
    private readonly IInvoiceRepository _invoices;
    private readonly IInvoiceQueries _invoiceQueries;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public MarkInvoicePaidCommandHandler(
        IInvoiceRepository invoices,
        IInvoiceQueries invoiceQueries,
        ISubscriptionRepository subscriptions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _invoices = invoices;
        _invoiceQueries = invoiceQueries;
        _subscriptions = subscriptions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<Result<InvoiceDetail>> HandleAsync(
        MarkInvoicePaidCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Validate-first (the M3.2 layer order): a caller who forgot the reference gets a 400 naming
        // the field, before any load or precondition spends work on the request.
        Result reference = BusinessRules.Validate(
            new PaymentReferenceMustBePresent(command.PaymentReference));
        if (reference.IsFailure)
        {
            return Result.Failure<InvoiceDetail>(reference.Error!);
        }

        Invoice? invoice = await _invoices.FindByIdAsync(command.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return Result.Failure<InvoiceDetail>(InvoiceErrors.NotFound(command.InvoiceId));
        }

        Result version = InvoiceWrites.ApplyExpectedVersion(
            _invoices, invoice, command.ExpectedRowVersion, versionRequired: false);
        if (version.IsFailure)
        {
            return Result.Failure<InvoiceDetail>(version.Error!);
        }

        // Guarded before the domain call so the caller gets the precise code — the transition table
        // would refuse both, but "already paid" and "not payable" are different conversations.
        if (invoice.Status == InvoiceStatus.Paid)
        {
            return Result.Failure<InvoiceDetail>(InvoiceErrors.AlreadyPaid);
        }

        if (invoice.Status != InvoiceStatus.Issued)
        {
            return Result.Failure<InvoiceDetail>(InvoiceErrors.NotPayable);
        }

        try
        {
            invoice.MarkPaid(command.PaymentReference!, _clock.UtcNow);
        }
        catch (DomainException exception)
        {
            return Result.Failure<InvoiceDetail>(
                Error.Conflict("invoices.invalid_state", exception.Message));
        }

        // Paying the current period's invoice is what "the payment arrived" means, so a past-due
        // subscription recovers in the same transaction. An *old* invoice settling does not: the
        // subscription is past due about the current period, not the archive.
        GymSubscription? subscription = await _subscriptions.FindByIdAsync(
            invoice.SubscriptionId, cancellationToken);
        if (subscription is not null
            && subscription.Status == SubscriptionStatus.PastDue
            && invoice.IssuedAtUtc >= subscription.CurrentPeriodStartUtc)
        {
            subscription.ResumePayment();
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await InvoiceWrites.ReadBackAsync(_invoiceQueries, invoice.Id, cancellationToken);
    }
}

internal sealed class VoidInvoiceCommandHandler
    : ICommandHandler<VoidInvoiceCommand, Result<InvoiceDetail>>
{
    private readonly IInvoiceRepository _invoices;
    private readonly IInvoiceQueries _invoiceQueries;
    private readonly IUnitOfWork _unitOfWork;

    public VoidInvoiceCommandHandler(
        IInvoiceRepository invoices,
        IInvoiceQueries invoiceQueries,
        IUnitOfWork unitOfWork)
    {
        _invoices = invoices;
        _invoiceQueries = invoiceQueries;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<InvoiceDetail>> HandleAsync(
        VoidInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Invoice? invoice = await _invoices.FindByIdAsync(command.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return Result.Failure<InvoiceDetail>(InvoiceErrors.NotFound(command.InvoiceId));
        }

        // Ordinal, like the gym-deletion phrase: an invoice number is an identifier, and identifiers
        // do not get culture-aware charity.
        if (!string.Equals(command.ConfirmAction, invoice.InvoiceNumber, StringComparison.Ordinal))
        {
            return Result.Failure<InvoiceDetail>(InvoiceErrors.ConfirmationMismatch);
        }

        Result version = InvoiceWrites.ApplyExpectedVersion(
            _invoices, invoice, command.ExpectedRowVersion, versionRequired: false);
        if (version.IsFailure)
        {
            return Result.Failure<InvoiceDetail>(version.Error!);
        }

        if (invoice.Status == InvoiceStatus.Paid)
        {
            return Result.Failure<InvoiceDetail>(InvoiceErrors.AlreadyPaid);
        }

        if (invoice.Status == InvoiceStatus.Void)
        {
            return Result.Failure<InvoiceDetail>(InvoiceErrors.AlreadyVoid);
        }

        try
        {
            invoice.MarkVoid();
        }
        catch (DomainException exception)
        {
            return Result.Failure<InvoiceDetail>(
                Error.Conflict("invoices.invalid_state", exception.Message));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await InvoiceWrites.ReadBackAsync(_invoiceQueries, invoice.Id, cancellationToken);
    }
}

internal sealed class ListInvoicesQueryHandler
    : IQueryHandler<ListInvoicesQuery, Result<PagedResult<InvoiceListItem>>>
{
    private readonly IInvoiceQueries _invoiceQueries;

    public ListInvoicesQueryHandler(IInvoiceQueries invoiceQueries) => _invoiceQueries = invoiceQueries;

    public async Task<Result<PagedResult<InvoiceListItem>>> HandleAsync(
        ListInvoicesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // An unknown status is a 400 naming the mistake, never a silently empty page — an operator
        // filtering on a typo should learn about the typo, not conclude nothing matches.
        if (query.Filter.Status is { } status
            && !Enum.TryParse<InvoiceStatus>(status, ignoreCase: true, out _))
        {
            return Result.Failure<PagedResult<InvoiceListItem>>(InvoiceErrors.StatusInvalid(status));
        }

        PagedResult<InvoiceListItem> page = await _invoiceQueries.ListAsync(
            query.Filter, cancellationToken);

        return Result.Success(page);
    }
}

internal sealed class GetInvoiceQueryHandler : IQueryHandler<GetInvoiceQuery, Result<InvoiceDetail>>
{
    private readonly IInvoiceQueries _invoiceQueries;

    public GetInvoiceQueryHandler(IInvoiceQueries invoiceQueries) => _invoiceQueries = invoiceQueries;

    public async Task<Result<InvoiceDetail>> HandleAsync(
        GetInvoiceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        InvoiceDetail? detail = await _invoiceQueries.GetAsync(query.InvoiceId, cancellationToken);

        return detail is null
            ? Result.Failure<InvoiceDetail>(InvoiceErrors.NotFound(query.InvoiceId))
            : Result.Success(detail);
    }
}

internal sealed class ListGymInvoicesQueryHandler
    : IQueryHandler<ListGymInvoicesQuery, Result<IReadOnlyList<InvoiceListItem>>>
{
    private readonly IInvoiceQueries _invoiceQueries;

    public ListGymInvoicesQueryHandler(IInvoiceQueries invoiceQueries) => _invoiceQueries = invoiceQueries;

    public async Task<Result<IReadOnlyList<InvoiceListItem>>> HandleAsync(
        ListGymInvoicesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<InvoiceListItem> invoices = await _invoiceQueries.ListForGymAsync(
            query.GymId, cancellationToken);

        return Result.Success(invoices);
    }
}

/// <summary>The precondition and read-back every invoice write shares (SubscriptionWrites' shape).</summary>
internal static class InvoiceWrites
{
    public static Result ApplyExpectedVersion(
        IInvoiceRepository invoices,
        Invoice invoice,
        string? expectedVersion,
        bool versionRequired)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return versionRequired
                ? Result.Failure(Error.Conflict(
                    "concurrency.version_required",
                    "This update requires an If-Match header carrying the rowVersion you read. " +
                    "Re-read the invoice and send its version."))
                : Result.Success();
        }

        if (!RowVersion.TryParse(expectedVersion, out uint parsed))
        {
            return Result.Failure(Error.Validation(
                "concurrency.malformed_version",
                "The If-Match header is not a version token issued by this API. " +
                "Send the rowVersion from the resource, quoted, without a weak (W/) prefix."));
        }

        invoices.ExpectVersion(invoice, parsed);
        return Result.Success();
    }

    public static async Task<Result<InvoiceDetail>> ReadBackAsync(
        IInvoiceQueries invoiceQueries,
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        InvoiceDetail? detail = await invoiceQueries.GetAsync(invoiceId, cancellationToken);

        return detail is null
            ? Result.Failure<InvoiceDetail>(InvoiceErrors.NotFound(invoiceId))
            : Result.Success(detail);
    }
}
