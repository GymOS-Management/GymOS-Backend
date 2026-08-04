using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Identity.Users;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// The second enforcement layer (AD-5). These tests exist because the first layer —
/// the endpoint policy — is exactly the kind of thing that gets forgotten on a new route,
/// and the consequence of forgetting it must be a refusal rather than an open write endpoint.
/// </summary>
public sealed class AuthorizationBehaviorTests
{
    [Fact]
    public async Task ARequestDeclaringAPermission_IsRefusedWhenTheCallerLacksIt()
    {
        var context = new IdentityTestContext();
        context.ActingAs(Guid.NewGuid()); // authenticated, but holds nothing

        // Thrown, not returned: reaching a handler without its permission means the route is
        // misconfigured, and a defect should be loud rather than shaped like a normal answer.
        PermissionDeniedException denied = await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.Dispatcher.SendAsync(
                new UpdateUserCommand(Guid.NewGuid(), "New", "Name"), CancellationToken.None));

        Assert.Equal(PlatformPermissions.UsersManage, denied.Permission);
        Assert.Equal(nameof(UpdateUserCommand), denied.RequestName);
    }

    [Fact]
    public async Task ARequestDeclaringAPermission_ProceedsWhenTheCallerHoldsIt()
    {
        var context = new IdentityTestContext();
        User target = context.GivenActiveUser("target@gymos.test");
        context.ActingAs(Guid.NewGuid(), PlatformPermissions.UsersManage);

        Result result = await context.Dispatcher.SendAsync(
            new UpdateUserCommand(target.Id, "Renamed", "Administrator"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Renamed", target.FirstName);
    }

    [Fact]
    public async Task AnUnauthenticatedCaller_IsRefusedWithoutQueryingPermissions()
    {
        var context = new IdentityTestContext();

        // No ActingAs: nobody is signed in. An unauthenticated caller can hold nothing, so
        // the behavior refuses without a lookup.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.Dispatcher.SendAsync(
                new SuspendUserCommand(Guid.NewGuid(), "because"), CancellationToken.None));
    }

    [Fact]
    public async Task HoldingADifferentPermission_DoesNotSatisfyTheRequirement()
    {
        var context = new IdentityTestContext();
        context.ActingAs(Guid.NewGuid(), PlatformPermissions.UsersRead);

        // read is not manage — permissions are explicit strings with no implied hierarchy
        // (AD-22: no wildcards, because a hierarchy makes "who can do X?" unanswerable).
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            context.Dispatcher.SendAsync(
                new UpdateUserCommand(Guid.NewGuid(), "New", "Name"), CancellationToken.None));
    }

    [Fact]
    public async Task ARequestExemptedFromPermissions_RunsForAnAnonymousCaller()
    {
        var context = new IdentityTestContext();
        context.GivenActiveUser(password: "correct-horse-battery");

        // Sign-in carries [NoPermissionRequired]; requiring a permission to obtain
        // permissions would be circular.
        Result<Identity.AuthenticatedSession> result = await context.Dispatcher.SendAsync(
            new Identity.SignIn.SignInCommand("admin@gymos.test", "correct-horse-battery", "203.0.113.1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void EveryRequestTypeDeclaresItsPermissionOrAnExplicitExemption()
    {
        // Rule R11, deny by default. A new command that declares neither fails here rather
        // than shipping as an unguarded endpoint — the failure names it, so the fix is
        // obvious.
        var undeclared = typeof(AssemblyMarker).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(type => type.GetInterfaces().Any(contract =>
                contract.IsGenericType &&
                (contract.GetGenericTypeDefinition() == typeof(ICommand<>) ||
                 contract.GetGenericTypeDefinition() == typeof(IQuery<>))))
            .Where(type =>
                type.GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: true).Length == 0 &&
                type.GetCustomAttributes(typeof(NoPermissionRequiredAttribute), inherit: true).Length == 0)
            .Select(type => type.Name)
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            "Requests declaring neither [RequiresPermission] nor [NoPermissionRequired]: " +
            string.Join(", ", undeclared));
    }
}
