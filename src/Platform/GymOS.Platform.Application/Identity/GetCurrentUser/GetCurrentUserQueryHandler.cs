using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.GetCurrentUser;

internal sealed class GetCurrentUserQueryHandler : IQueryHandler<GetCurrentUserQuery, Result<CurrentUser>>
{
    private readonly IIdentityQueries _identityQueries;

    public GetCurrentUserQueryHandler(IIdentityQueries identityQueries) => _identityQueries = identityQueries;

    public async Task<Result<CurrentUser>> HandleAsync(
        GetCurrentUserQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        UserProfile? profile = await _identityQueries.GetProfileAsync(query.UserId, cancellationToken);
        if (profile is null)
        {
            // A valid token whose subject no longer exists: the user was deleted
            // mid-session. Reported honestly rather than as an empty profile.
            return Result.Failure<CurrentUser>(AuthenticationErrors.UserNotFound);
        }

        IReadOnlyCollection<string> permissions =
            await _identityQueries.GetEffectivePermissionsAsync(query.UserId, cancellationToken);

        return Result.Success(new CurrentUser(profile, permissions));
    }
}
