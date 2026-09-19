using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Gyms;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// Managing gyms — which are tenants, so the interesting cases are the refusals: what stops an
/// ordinary-looking edit from taking a paying business offline or handing its address to someone else.
/// </summary>
public sealed class GymManagementTests
{
    private const string Read = PlatformPermissions.GymsRead;
    private const string Create = PlatformPermissions.GymsCreate;
    private const string Update = PlatformPermissions.GymsUpdate;
    private const string Suspend = PlatformPermissions.GymsSuspend;
    private const string Delete = PlatformPermissions.GymsDelete;

    private static ProvisionGymCommand Provision(string slug = "new-gym") => new(
        slug,
        "New Gym",
        "Ada Owner",
        "ada@newgym.test",
        "+357 99 123456",
        "CY",
        "Asia/Nicosia",
        "EUR");

    // ── Provisioning ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProvisionedGym_StartsInProvisioningWithAnEmptyBrandingRecord()
    {
        var context = new GymTestContext().ActingAs(Create);

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            Provision(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(GymStatus.Provisioning), result.Value.Status);

        Gym created = Assert.Single(context.Gyms.Gyms);
        Assert.Equal("new-gym", created.Slug);

        // Created alongside the gym, so the detail view and the branding endpoint always have a record
        // to read and write rather than every caller needing a create-or-update path.
        GymBranding branding = Assert.Single(context.Gyms.Branding);
        Assert.Equal(created.Id, branding.GymId);
    }

    [Fact]
    public async Task ProvisioningATakenSlug_IsAConflict()
    {
        var context = new GymTestContext().ActingAs(Create);
        context.GivenActiveGym("acme-gym");

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            Provision("acme-gym"),
            CancellationToken.None);

        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal("gyms.slug_taken", result.Error.Code);
        Assert.Single(context.Gyms.Gyms);
    }

    [Fact]
    public async Task ADeletedGymStillHoldsItsSlug()
    {
        var context = new GymTestContext().ActingAs(Create);
        context.GivenDeletedGym("acme-gym");

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            Provision("acme-gym"),
            CancellationToken.None);

        // A slug is never released: it may still be in a member's bookmark, and handing it to a
        // different gym would point them at someone else's business.
        Assert.Equal("gyms.slug_taken", result.Error!.Code);
    }

    [Theory]
    [InlineData("Acme-Gym")]
    [InlineData("ab")]
    [InlineData("acme--gym")]
    public async Task ProvisioningAnInvalidSlug_IsAValidationFailure_NotAnException(string slug)
    {
        var context = new GymTestContext().ActingAs(Create);

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            Provision(slug),
            CancellationToken.None);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("gyms.slug_invalid", result.Error.Code);
        Assert.Empty(context.Gyms.Gyms);
    }

    [Fact]
    public async Task ProvisioningWithABrokenLocale_ReportsWhichValueIsWrong()
    {
        var context = new GymTestContext().ActingAs(Create);

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            Provision() with { Currency = "EURO" },
            CancellationToken.None);

        // The rule's own code travels to the caller, so a form can highlight the field that failed.
        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("gyms.currency_invalid", result.Error.Code);
        Assert.Empty(context.Gyms.Gyms);
    }

    [Fact]
    public async Task ProvisioningWithoutThePermission_IsRefused()
    {
        var context = new GymTestContext().ActingAs(Read);

        // Deny by default: reading gyms does not imply creating one. The pipeline refuses before the
        // handler runs, which is why this is an exception rather than a failed Result — nothing got far
        // enough to produce one.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.Dispatcher.SendAsync(Provision(), CancellationToken.None));

        Assert.Empty(context.Gyms.Gyms);
    }

    // ── Updating details ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatingAGym_RequiresAnExpectedVersion()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new UpdateGymCommand(
                gym.Id, "Renamed", "Ada", "ada@acme.test", null, "CY", "Asia/Nicosia", "EUR", null),
            CancellationToken.None);

        // A PUT overwrites fields the caller read, so proceeding without a version *is*
        // last-write-wins and is refused.
        Assert.Equal("concurrency.version_required", result.Error!.Code);
        Assert.Equal("Acme Gym", gym.Name);
    }

    [Fact]
    public async Task UpdatingAGym_RejectsAVersionTokenThisApiNeverIssued()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new UpdateGymCommand(
                gym.Id, "Renamed", "Ada", "ada@acme.test", null, "CY", "Asia/Nicosia", "EUR", "W/\"7\""),
            CancellationToken.None);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("concurrency.malformed_version", result.Error.Code);
    }

    [Fact]
    public async Task UpdatingAGym_AppliesTheChangeAndKeepsTheSlug()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new UpdateGymCommand(
                gym.Id, "Acme Fitness", "Grace", "grace@acme.test", null, "GR", "Europe/Athens", "EUR", "1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Acme Fitness", result.Value.Name);
        Assert.Equal("acme-gym", result.Value.Slug);
        Assert.Equal(1u, context.Gyms.ExpectedVersions[gym.Id]);
        Assert.Equal(1, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task UpdatingADeletedGym_IsRefused()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenDeletedGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new UpdateGymCommand(
                gym.Id, "Renamed", "Ada", "ada@acme.test", null, "CY", "Asia/Nicosia", "EUR", "1"),
            CancellationToken.None);

        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal("gyms.deleted", result.Error.Code);
    }

    [Fact]
    public async Task UpdatingAnUnknownGym_IsNotFound()
    {
        var context = new GymTestContext().ActingAs(Update);

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new UpdateGymCommand(
                Guid.NewGuid(), "Renamed", "Ada", "ada@acme.test", null, "CY", "Asia/Nicosia", "EUR", "1"),
            CancellationToken.None);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
        Assert.Equal("gyms.not_found", result.Error.Code);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ActivatingAProvisioningGym_OpensItForBusiness()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenProvisioningGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new ActivateGymCommand(gym.Id, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(GymStatus.Active), result.Value.Status);
        Assert.True(gym.IsOperational);
    }

    [Fact]
    public async Task ActivatingAnAlreadyActiveGym_IsAConflict()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new ActivateGymCommand(gym.Id, null),
            CancellationToken.None);

        Assert.Equal("gyms.already_active", result.Error!.Code);
    }

    [Fact]
    public async Task ActivatingASuspendedGym_DirectsTheCallerToReactivate()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenSuspendedGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new ActivateGymCommand(gym.Id, null),
            CancellationToken.None);

        // Lifting a suspension is an operator decision about a dispute, not the same act as finishing
        // setup — and it needs a different permission.
        Assert.Equal("gyms.already_suspended", result.Error!.Code);
        Assert.Equal(GymStatus.Suspended, gym.Status);
    }

    [Fact]
    public async Task SuspendingAGym_RecordsTheReasonAndReturnsIt()
    {
        var context = new GymTestContext().ActingAs(Suspend);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new SuspendGymCommand(gym.Id, "Non-payment, invoice 1042", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(GymStatus.Suspended), result.Value.Status);
        Assert.Equal("Non-payment, invoice 1042", result.Value.SuspensionReason);
    }

    [Fact]
    public async Task SuspendingWithoutAReason_IsRefused()
    {
        var context = new GymTestContext().ActingAs(Suspend);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new SuspendGymCommand(gym.Id, "   ", null),
            CancellationToken.None);

        Assert.Equal("gyms.reason_required", result.Error!.Code);
        Assert.Equal(GymStatus.Active, gym.Status);
    }

    [Fact]
    public async Task SuspendingWithTheUpdatePermissionAlone_IsRefused()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenActiveGym();

        // Taking a business offline is its own permission: editing a gym's phone number should not
        // carry the authority to stop it trading.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.Dispatcher.SendAsync(
                new SuspendGymCommand(gym.Id, "Non-payment", null),
                CancellationToken.None));

        Assert.Equal(GymStatus.Active, gym.Status);
    }

    [Fact]
    public async Task SuspendingHonoursAVersionWhenGiven_ButDoesNotDemandOne()
    {
        var context = new GymTestContext().ActingAs(Suspend);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new SuspendGymCommand(gym.Id, "Non-payment", "1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1u, context.Gyms.ExpectedVersions[gym.Id]);
    }

    [Fact]
    public async Task ReactivatingAGymThatIsNotSuspended_IsAConflict()
    {
        var context = new GymTestContext().ActingAs(Suspend);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new ReactivateGymCommand(gym.Id, null),
            CancellationToken.None);

        Assert.Equal("gyms.not_suspended", result.Error!.Code);
    }

    [Fact]
    public async Task ReactivatingASuspendedGym_ClearsTheReason()
    {
        var context = new GymTestContext().ActingAs(Suspend);
        Gym gym = context.GivenSuspendedGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new ReactivateGymCommand(gym.Id, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(GymStatus.Active), result.Value.Status);
        Assert.Null(result.Value.SuspensionReason);
    }

    // ── Deletion ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeletingWithoutTheConfirmation_IsRefusedAndSaysWhatToSend()
    {
        var context = new GymTestContext().ActingAs(Delete);
        Gym gym = context.GivenActiveGym();

        Result result = await context.Dispatcher.SendAsync(
            new DeleteGymCommand(gym.Id, null, null),
            CancellationToken.None);

        Assert.Equal("gyms.confirmation_required", result.Error!.Code);
        Assert.Contains("acme-gym", result.Error.Message, StringComparison.Ordinal);
        Assert.False(gym.IsDeleted);
    }

    [Fact]
    public async Task DeletingWithTheWrongConfirmation_ChangesNothing()
    {
        var context = new GymTestContext().ActingAs(Delete);
        Gym gym = context.GivenActiveGym();

        Result result = await context.Dispatcher.SendAsync(
            new DeleteGymCommand(gym.Id, "some-other-gym", null),
            CancellationToken.None);

        // The point of the step-up: a replayed call or a mis-clicked row names the wrong gym.
        Assert.Equal("gyms.confirmation_mismatch", result.Error!.Code);
        Assert.False(gym.IsDeleted);
        Assert.Equal(0, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task DeletingWithTheSlug_SoftDeletesAndStampsTheTime()
    {
        var context = new GymTestContext().ActingAs(Delete);
        Gym gym = context.GivenActiveGym();

        Result result = await context.Dispatcher.SendAsync(
            new DeleteGymCommand(gym.Id, "acme-gym", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(gym.IsDeleted);
        Assert.Equal(GymTestContext.Start, gym.DeletedAtUtc);
    }

    [Fact]
    public async Task DeletingAGym_EndsTheSubscriptionItWasPayingFor()
    {
        var context = new GymTestContext().ActingAs(Delete);
        Gym gym = context.GivenActiveGym();

        SubscriptionPlan plan = SubscriptionPlan.Create("PRO", "PRO", "", 0, PlanLimits.Unlimited);
        plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Currency.Euro));
        GymSubscription subscription = GymSubscription.Assign(
            gym.Id, plan, SubscriptionTerms.From(plan.Prices.Single()), GymTestContext.Start);
        context.Subscriptions.Seed(subscription);

        Result result = await context.Dispatcher.SendAsync(
            new DeleteGymCommand(gym.Id, "acme-gym", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(gym.IsDeleted);

        // A deleted gym must stop costing money: left live, the renewal sweep would advance the
        // period and issue an invoice against a business that no longer exists, every cycle.
        Assert.Equal(SubscriptionStatus.Canceled, subscription.Status);
        Assert.False(subscription.IsLive);
        Assert.Equal(GymTestContext.Start, subscription.CanceledAtUtc);

        // One transaction: a deleted gym holding a live subscription must never be a state that
        // exists between two commits.
        Assert.Equal(1, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task DeletingAGym_AsksTheMaintainerToWithdrawWhatThePlanMadeRedundant()
    {
        var context = new GymTestContext().ActingAs(Delete);
        Gym gym = context.GivenActiveGym();

        Result result = await context.Dispatcher.SendAsync(
            new DeleteGymCommand(gym.Id, "acme-gym", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        // With no live plan after deletion — so every override is judged against the defaults alone.
        // GymFeatureLifecycleTests sees the outcome; this pins that the handler asked, once, correctly.
        Assert.Equal(gym.Id, Assert.Single(context.FeatureExceptions.CalledForGymIds));
        Assert.Null(Assert.Single(context.FeatureExceptions.CalledWithLivePlanIds));
    }

    [Fact]
    public async Task DeletingAnAlreadyDeletedGym_Succeeds()
    {
        var context = new GymTestContext().ActingAs(Delete);
        Gym gym = context.GivenDeletedGym();

        Result result = await context.Dispatcher.SendAsync(
            new DeleteGymCommand(gym.Id, "acme-gym", null),
            CancellationToken.None);

        // Idempotent: an operator who deleted a gym and retried should be told it is done, not shown a
        // failure for having succeeded.
        Assert.True(result.IsSuccess);
        Assert.Equal(0, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task DeletingWithTheSuspendPermission_IsRefused()
    {
        var context = new GymTestContext().ActingAs(Suspend);
        Gym gym = context.GivenActiveGym();

        // Suspension is reversible; deletion is not. They are deliberately different permissions, and
        // PlatformAdmin holds the first but not the second.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.Dispatcher.SendAsync(
                new DeleteGymCommand(gym.Id, "acme-gym", null),
                CancellationToken.None));

        Assert.False(gym.IsDeleted);
    }

    // ── Branding ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatingBranding_ReplacesItAndAdvancesTheGymsVersion()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new UpdateGymBrandingCommand(
                gym.Id, "logo.png", null, "#1A2B3C", null, "acme.com", "Acme Gym", "1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("#1A2B3C", result.Value.Branding.PrimaryColour);
        Assert.Equal("acme.com", result.Value.Branding.CustomDomain);

        // Without this the gym's row version does not move, and two operators editing branding
        // concurrently both succeed.
        Assert.Contains(gym.Id, context.Gyms.AggregateChanges);
    }

    [Fact]
    public async Task UpdatingBranding_RequiresAnExpectedVersion()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new UpdateGymBrandingCommand(gym.Id, null, null, "#1A2B3C", null, null, null, null),
            CancellationToken.None);

        Assert.Equal("concurrency.version_required", result.Error!.Code);
    }

    [Fact]
    public async Task ClaimingAnotherGymsCustomDomain_IsAConflict()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym incumbent = context.GivenActiveGym("incumbent");
        Gym challenger = context.GivenActiveGym("challenger");

        await context.Dispatcher.SendAsync(
            new UpdateGymBrandingCommand(incumbent.Id, null, null, null, null, "acme.com", null, "1"),
            CancellationToken.None);

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new UpdateGymBrandingCommand(challenger.Id, null, null, null, null, "ACME.com", null, "1"),
            CancellationToken.None);

        // A domain must resolve to exactly one gym, and hostnames are case-insensitive.
        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal("gyms.custom_domain_taken", result.Error.Code);
    }

    [Fact]
    public async Task KeepingYourOwnCustomDomain_IsNotAConflict()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenActiveGym();

        await context.Dispatcher.SendAsync(
            new UpdateGymBrandingCommand(gym.Id, null, null, null, null, "acme.com", null, "1"),
            CancellationToken.None);

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new UpdateGymBrandingCommand(gym.Id, null, null, "#1A2B3C", null, "acme.com", null, "1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("acme.com", result.Value.Branding.CustomDomain);
    }

    [Fact]
    public async Task UpdatingBrandingWithAnInvalidColour_IsAValidationFailure()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new UpdateGymBrandingCommand(gym.Id, null, null, "rebeccapurple", null, null, null, "1"),
            CancellationToken.None);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("gyms.colour_invalid", result.Error.Code);
    }

    [Fact]
    public async Task UpdatingADeletedGymsBranding_IsRefused()
    {
        var context = new GymTestContext().ActingAs(Update);
        Gym gym = context.GivenDeletedGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            new UpdateGymBrandingCommand(gym.Id, null, null, "#1A2B3C", null, null, null, "1"),
            CancellationToken.None);

        Assert.Equal("gyms.deleted", result.Error!.Code);
    }

    // ── Reading ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GettingADeletedGym_StillReturnsIt()
    {
        var context = new GymTestContext().ActingAs(Read);
        Gym gym = context.GivenDeletedGym();

        Result<GymDetail> result = await context.Dispatcher.QueryAsync(
            new GetGymQuery(gym.Id),
            CancellationToken.None);

        // An operator investigating why a tenant stopped working needs to be told it was deleted, and
        // when — not that it never existed.
        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(GymStatus.Deleted), result.Value.Status);
        Assert.NotNull(result.Value.DeletedAtUtc);
    }

    [Fact]
    public async Task ListingExcludesDeletedGymsByDefault()
    {
        var context = new GymTestContext().ActingAs(Read);
        context.GivenActiveGym("live-gym");
        context.GivenDeletedGym("gone-gym");

        Result<PagedResult<GymListItem>> result =
            await context.Dispatcher.QueryAsync(
                new ListGymsQuery(new PageRequest(null, null, null, null, null), null, null, false),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("live-gym", Assert.Single(result.Value.Items).Slug);
    }

    [Fact]
    public async Task ListingCanIncludeDeletedGymsDeliberately()
    {
        var context = new GymTestContext().ActingAs(Read);
        context.GivenActiveGym("live-gym");
        context.GivenDeletedGym("gone-gym");

        Result<PagedResult<GymListItem>> result =
            await context.Dispatcher.QueryAsync(
                new ListGymsQuery(new PageRequest(null, null, null, null, null), null, null, true),
                CancellationToken.None);

        Assert.Equal(2, result.Value.Items.Count);
    }

    [Fact]
    public async Task ReadingWithoutThePermission_IsRefused()
    {
        var context = new GymTestContext();

        // Reads are guarded too: a gym's owner name and contact details are commercial information.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.Dispatcher.QueryAsync(new GetGymQuery(Guid.NewGuid()), CancellationToken.None));
    }

    // ── The features.read gate on write routes ──────────────────────────────────

    private const string FeaturesRead = PlatformPermissions.FeaturesRead;

    // TheoryData<string, Func<GymTestContext, Gym, ICommand<Result<GymDetail>>>> does not compile here:
    // GymTestContext is internal, and a public Theory method (xUnit requires public) cannot take a
    // parameter whose type argument is less accessible than the method (CS0051). InlineData plus this
    // private route-dispatch switch keeps the same six routes and the same two assertions per route
    // without putting GymTestContext into any public signature.
    private static ICommand<Result<GymDetail>> WriteRouteCommand(string route, GymTestContext context, Gym gym) =>
        route switch
        {
            "provision" => Provision("features-gym"),
            "update" => new UpdateGymCommand(
                gym.Id, "Acme Fitness", "Grace", "grace@acme.test", null, "GR", "Europe/Athens", "EUR", "1"),
            "activate" => new ActivateGymCommand(context.GivenProvisioningGym("activate-gym").Id, null),
            "suspend" => new SuspendGymCommand(gym.Id, "Non-payment", null),
            "reactivate" => new ReactivateGymCommand(context.GivenSuspendedGym("reactivate-gym").Id, null),
            "branding" => new UpdateGymBrandingCommand(gym.Id, "logo.png", null, "#1A2B3C", null, null, null, "1"),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown write route."),
        };

    [Theory]
    [InlineData("provision")]
    [InlineData("update")]
    [InlineData("activate")]
    [InlineData("suspend")]
    [InlineData("reactivate")]
    [InlineData("branding")]
    public async Task AWriterHoldingFeaturesRead_GetsTheResolvedFeaturesBack(string route)
    {
        // GymWrites.ReadBackAsync asks IPermissionEvaluator for features.read and includes the resolved
        // catalogue only when it is held. Until H1 the fake returned null whether or not it was asked,
        // so this branch was never exercised on any write route.
        var context = new GymTestContext().ActingAs(Create, Update, Suspend, FeaturesRead).WithFeatureCatalogue();
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            WriteRouteCommand(route, context, gym), CancellationToken.None);

        Assert.True(result.IsSuccess, route);
        Assert.NotNull(result.Value.Features);
    }

    [Theory]
    [InlineData("provision")]
    [InlineData("update")]
    [InlineData("activate")]
    [InlineData("suspend")]
    [InlineData("reactivate")]
    [InlineData("branding")]
    public async Task AWriterWithoutFeaturesRead_GetsNullFeatures(string route)
    {
        // The other half of the gate: no features.read, no features — and the fake would throw if the
        // handler asked for them anyway, so a null here is a real null.
        var context = new GymTestContext().ActingAs(Create, Update, Suspend);
        Gym gym = context.GivenActiveGym();

        Result<GymDetail> result = await context.Dispatcher.SendAsync(
            WriteRouteCommand(route, context, gym), CancellationToken.None);

        Assert.True(result.IsSuccess, route);
        Assert.Null(result.Value.Features);
    }
}
