using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.DependencyInjection;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Application.Invoices;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Application.UnitTests.Fakes;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Results;
using GymOS.TestingSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// The two things that can happen to an issued invoice — payment and voiding — and the read side.
/// Mark-paid is the platform's first production <see cref="IIdempotentCommand"/>; its wire contract
/// is pinned here so a change to it is a deliberate API decision, not a refactor's side effect.
/// </summary>
public sealed class InvoiceCommandTests
{
    private const string Manage = PlatformPermissions.BillingManage;
    private const string Read = PlatformPermissions.BillingRead;

    private sealed class Context
    {
        private readonly ServiceProvider _provider;

        public Context(params string[] permissions)
        {
            InvoiceQueries = new FakeInvoiceQueries(Invoices, Gyms, Clock);

            var services = new ServiceCollection();
            services.AddCqrsDispatcher();
            services.AddAuthorizationBehavior();
            services.AddPlatformApplication();

            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IGymRepository>(Gyms);
            services.AddSingleton<IGymQueries>(new FakeGymQueries(Gyms, Permissions));
            services.AddSingleton<IPlanRepository>(Plans);
            services.AddSingleton<ISubscriptionRepository>(Subscriptions);
            services.AddSingleton<ISubscriptionQueries>(
                new FakeSubscriptionQueries(Subscriptions, Plans));
            services.AddSingleton<IInvoiceRepository>(Invoices);
            services.AddSingleton<IInvoiceNumberGenerator>(new FakeInvoiceNumberGenerator());
            services.AddSingleton<IInvoiceQueries>(InvoiceQueries);
            services.AddSingleton<IUnitOfWork>(UnitOfWork);
            services.AddSingleton<ICurrentUser>(CurrentUser);
            services.AddSingleton<IPermissionEvaluator>(Permissions);
            services.AddSingleton<IAuditTrail>(new FakeAuditTrail());
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

            _provider = services.BuildServiceProvider();

            CurrentUser.UserId = Guid.NewGuid();
            foreach (string permission in permissions)
            {
                Permissions.Granted.Add(permission);
            }
        }

        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));

        public FakeGymRepository Gyms { get; } = new();

        public FakePlanRepository Plans { get; } = new();

        public FakeSubscriptionRepository Subscriptions { get; } = new();

        public FakeInvoiceRepository Invoices { get; } = new();

        public FakeInvoiceQueries InvoiceQueries { get; }

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public FakeCurrentUser CurrentUser { get; } = new();

        public FakePermissionEvaluator Permissions { get; } = new();

        public IDispatcher Dispatcher => _provider.GetRequiredService<IDispatcher>();

        public Gym GivenGym()
        {
            Gym gym = Gym.Provision(
                "acme-gym",
                "Acme Gym",
                new GymOwnerContact("Ada", "ada@acme.test", null),
                new GymLocale("CY", "Asia/Nicosia", "EUR"));

            gym.Activate();
            Gyms.Seed(gym);
            return gym;
        }

        public GymSubscription GivenSubscription(Gym gym, bool pastDue = false)
        {
            SubscriptionPlan plan = SubscriptionPlan.Create("PRO", "PRO", "", 0, PlanLimits.Unlimited);
            plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Currency.Euro));
            Plans.Seed(plan);

            GymSubscription subscription = GymSubscription.Assign(
                gym.Id, plan, SubscriptionTerms.From(plan.Prices.Single()), Clock.UtcNow);

            if (pastDue)
            {
                subscription.MarkPastDue();
            }

            Subscriptions.Seed(subscription);
            return subscription;
        }

        /// <summary>An issued invoice for one period at 49.99 EUR, seeded straight into the fake.</summary>
        public Invoice GivenInvoice(
            Gym gym,
            GymSubscription subscription,
            string number = "2026-000001",
            DateTimeOffset? issuedAt = null)
        {
            DateTimeOffset issued = issuedAt ?? Clock.UtcNow;

            Invoice invoice = Invoice.Issue(
                gym.Id,
                subscription.Id,
                number,
                [new InvoiceLineSpec("PRO — 2026-08-06 to 2026-09-06", 1, Money.Of(49.99m, Currency.Euro))],
                0m,
                issued,
                issued.AddDays(InvoicePaymentTerms.Days));

            Invoices.Seed(invoice);
            return invoice;
        }

        public Task<Result<InvoiceDetail>> MarkPaidAsync(Guid invoiceId, string? reference = "BANK-2026-4711") =>
            Dispatcher.SendAsync(
                new MarkInvoicePaidCommand(invoiceId, reference, null), CancellationToken.None);

        public Task<Result<InvoiceDetail>> VoidAsync(Guid invoiceId, string? confirm) =>
            Dispatcher.SendAsync(
                new VoidInvoiceCommand(invoiceId, confirm, null), CancellationToken.None);
    }

    [Fact]
    public async Task MarkingPaid_RecordsTheReferenceAndReturnsTheDetail()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym);
        Invoice invoice = context.GivenInvoice(gym, subscription);

        Result<InvoiceDetail> result = await context.MarkPaidAsync(invoice.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("Paid", result.Value.Status);
        Assert.Equal("BANK-2026-4711", result.Value.PaymentReference);
        Assert.Equal(context.Clock.UtcNow, result.Value.PaidAtUtc);
        Assert.Equal(1, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task AnUnknownInvoice_IsNotFound()
    {
        var context = new Context(Manage);

        Result<InvoiceDetail> result = await context.MarkPaidAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("invoices.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task PayingTwice_IsRefusedByName()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym);
        Invoice invoice = context.GivenInvoice(gym, subscription);
        await context.MarkPaidAsync(invoice.Id);

        Result<InvoiceDetail> result = await context.MarkPaidAsync(invoice.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("invoices.already_paid", result.Error!.Code);
    }

    [Fact]
    public async Task PayingAVoidedInvoice_IsNotPayable()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym);
        Invoice invoice = context.GivenInvoice(gym, subscription);
        await context.VoidAsync(invoice.Id, invoice.InvoiceNumber);

        Result<InvoiceDetail> result = await context.MarkPaidAsync(invoice.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("invoices.not_payable", result.Error!.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task ABlankReference_IsAValidationFailure(string? reference)
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym);
        Invoice invoice = context.GivenInvoice(gym, subscription);

        Result<InvoiceDetail> result = await context.MarkPaidAsync(invoice.Id, reference);

        Assert.True(result.IsFailure);
        Assert.Equal("invoices.payment_reference_invalid", result.Error!.Code);
        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal(0, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task PayingTheCurrentPeriodsInvoice_RecoversAPastDueSubscription()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym, pastDue: true);
        Invoice invoice = context.GivenInvoice(gym, subscription);

        Result<InvoiceDetail> result = await context.MarkPaidAsync(invoice.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(1, context.UnitOfWork.SaveCount); // one transaction covers both
    }

    [Fact]
    public async Task PayingASideInvoice_LeavesPastDueUntilThePeriodIsSettled()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym, pastDue: true);
        Invoice main = context.GivenInvoice(gym, subscription, "2026-000001");
        Invoice proration = context.GivenInvoice(gym, subscription, "2026-000002");

        // Paying the small proration invoice settles nothing while the period's main invoice is
        // still open — the gym must not escape past-due status for 20 EUR of a 500 EUR debt.
        Result<InvoiceDetail> paidSide = await context.MarkPaidAsync(proration.Id);
        Assert.True(paidSide.IsSuccess);
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);

        // Paying the LAST open invoice of the period is what "the payment arrived" means.
        Result<InvoiceDetail> paidMain = await context.MarkPaidAsync(main.Id);
        Assert.True(paidMain.IsSuccess);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("2")]
    public async Task NumericStatusValues_AreRefusedLikeAnyOtherUnknown(string status)
    {
        // Enum.TryParse would accept any integer string; the filter validates against the NAMES,
        // so a numeric value cannot slip through to a silently empty page.
        var context = new Context(Read);

        Result<PagedResult<InvoiceListItem>> result = await context.Dispatcher.QueryAsync(
            new ListInvoicesQuery(new InvoiceListFilter(new PageRequest(), status, null, null)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("invoices.status_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task PayingAnOldInvoice_LeavesAPastDueSubscriptionPastDue()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym, pastDue: true);
        Invoice invoice = context.GivenInvoice(
            gym, subscription, issuedAt: subscription.CurrentPeriodStartUtc.AddMonths(-1));

        Result<InvoiceDetail> result = await context.MarkPaidAsync(invoice.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
    }

    [Fact]
    public async Task Voiding_RequiresTheInvoiceNumberAsConfirmation()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym);
        Invoice invoice = context.GivenInvoice(gym, subscription);

        Result<InvoiceDetail> wrong = await context.VoidAsync(invoice.Id, "2026-999999");
        Result<InvoiceDetail> missing = await context.VoidAsync(invoice.Id, null);
        Result<InvoiceDetail> right = await context.VoidAsync(invoice.Id, invoice.InvoiceNumber);

        Assert.Equal("invoices.confirmation_mismatch", wrong.Error!.Code);
        Assert.Equal("invoices.confirmation_mismatch", missing.Error!.Code);
        Assert.True(right.IsSuccess);
        Assert.Equal("Void", right.Value.Status);
    }

    [Fact]
    public async Task VoidingAPaidInvoice_IsRefusedAsAlreadyPaid()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym);
        Invoice invoice = context.GivenInvoice(gym, subscription);
        await context.MarkPaidAsync(invoice.Id);

        Result<InvoiceDetail> result = await context.VoidAsync(invoice.Id, invoice.InvoiceNumber);

        Assert.True(result.IsFailure);
        Assert.Equal("invoices.already_paid", result.Error!.Code);
    }

    [Fact]
    public async Task VoidingTheLastOpenInvoiceOfThePeriod_RecoversAPastDueSubscription()
    {
        // The invoice that flipped the subscription PastDue turns out to be wrong and is voided:
        // nothing in the period remains open, so the gym must not stay flagged delinquent — the
        // settled-period rule runs on the void path exactly as it does on payment.
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym, pastDue: true);
        Invoice invoice = context.GivenInvoice(gym, subscription);

        Result<InvoiceDetail> result = await context.VoidAsync(invoice.Id, invoice.InvoiceNumber);

        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Fact]
    public async Task VoidingASideInvoice_LeavesPastDueWhileTheMainInvoiceIsOpen()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym, pastDue: true);
        context.GivenInvoice(gym, subscription, "2026-000001"); // the main debt stays open
        Invoice side = context.GivenInvoice(gym, subscription, "2026-000002");

        Result<InvoiceDetail> result = await context.VoidAsync(side.Id, side.InvoiceNumber);

        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
    }

    [Fact]
    public async Task VoidingTwice_IsRefusedAsAlreadyVoid()
    {
        var context = new Context(Manage);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym);
        Invoice invoice = context.GivenInvoice(gym, subscription);
        await context.VoidAsync(invoice.Id, invoice.InvoiceNumber);

        Result<InvoiceDetail> result = await context.VoidAsync(invoice.Id, invoice.InvoiceNumber);

        Assert.True(result.IsFailure);
        Assert.Equal("invoices.already_void", result.Error!.Code);
    }

    [Fact]
    public void MarkPaid_PinsItsIdempotencyWireContract()
    {
        // Trivial on purpose: these three values are what a client's retry logic is written against,
        // so changing any of them must show up as a failing test, not a diff nobody reads.
        var command = new MarkInvoicePaidCommand(Guid.NewGuid(), "ref", null);

        Assert.Equal("billing.mark-invoice-paid", command.OperationKey);
        Assert.Equal(TimeSpan.FromHours(72), command.RetentionPeriod);
        Assert.True(command.KeyRequired);
    }

    [Fact]
    public async Task MarkingPaidWithoutTheManagePermission_IsRefused()
    {
        var context = new Context(Read);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym);
        Invoice invoice = context.GivenInvoice(gym, subscription);

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.MarkPaidAsync(invoice.Id));

        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
    }

    [Fact]
    public async Task Listing_FiltersByStatusAndRefusesAnUnknownOne()
    {
        var context = new Context(Manage, Read);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym);
        Invoice paid = context.GivenInvoice(gym, subscription, "2026-000001");
        context.GivenInvoice(gym, subscription, "2026-000002");
        await context.MarkPaidAsync(paid.Id);

        Result<PagedResult<InvoiceListItem>> issued = await context.Dispatcher.QueryAsync(
            new ListInvoicesQuery(new InvoiceListFilter(new PageRequest(), "issued", null, null)),
            CancellationToken.None);
        Result<PagedResult<InvoiceListItem>> unknown = await context.Dispatcher.QueryAsync(
            new ListInvoicesQuery(new InvoiceListFilter(new PageRequest(), "bogus", null, null)),
            CancellationToken.None);

        Assert.True(issued.IsSuccess);
        Assert.Equal("2026-000002", Assert.Single(issued.Value.Items).InvoiceNumber);
        Assert.True(unknown.IsFailure);
        Assert.Equal("invoices.status_invalid", unknown.Error!.Code);
    }

    [Fact]
    public async Task TheGymTab_ListsOnlyThatGymsInvoices()
    {
        var context = new Context(Manage, Read);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym);
        context.GivenInvoice(gym, subscription);

        Result<IReadOnlyList<InvoiceListItem>> mine = await context.Dispatcher.QueryAsync(
            new ListGymInvoicesQuery(gym.Id), CancellationToken.None);
        Result<IReadOnlyList<InvoiceListItem>> other = await context.Dispatcher.QueryAsync(
            new ListGymInvoicesQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.True(mine.IsSuccess);
        Assert.Equal("Acme Gym", Assert.Single(mine.Value).GymName);
        Assert.True(other.IsSuccess);
        Assert.Empty(other.Value);
    }

    [Fact]
    public async Task GettingOneInvoice_CarriesItsLines()
    {
        var context = new Context(Manage, Read);
        Gym gym = context.GivenGym();
        GymSubscription subscription = context.GivenSubscription(gym);
        Invoice invoice = context.GivenInvoice(gym, subscription);

        Result<InvoiceDetail> result = await context.Dispatcher.QueryAsync(
            new GetInvoiceQuery(invoice.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        InvoiceLineDetail line = Assert.Single(result.Value.Lines);
        Assert.Equal(49.99m, line.Amount);
        Assert.Equal("EUR", line.Currency);
        Assert.False(result.Value.IsOverdue);
    }
}
