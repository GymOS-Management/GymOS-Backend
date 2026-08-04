using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.Users;

/// <summary>
/// The two steps every user-write handler shares: honoring the caller's expected version, and
/// reading the resource back for the response.
///
/// Collected here so a new write endpoint cannot accidentally omit either. A handler that forgot
/// the version check would silently reintroduce last-write-wins on one route — the kind of gap
/// that is invisible until two administrators edit the same account.
/// </summary>
internal static class UserWrites
{
    /// <summary>
    /// Applies the caller's <c>If-Match</c> token, if the endpoint supplies one.
    ///
    /// <paramref name="versionRequired"/> separates the two cases deliberately. A PUT overwrites
    /// fields the caller previously read, so proceeding without a version *is* last-write-wins and
    /// is refused. An action endpoint (suspend, reactivate) changes state the domain state machine
    /// already guards, so a version is honored when given but not demanded — requiring a fresh GET
    /// before every suspension buys nothing the transition rules do not already provide.
    /// </summary>
    public static Result ApplyExpectedVersion(
        IUserRepository users,
        User user,
        string? expectedVersion,
        bool versionRequired)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return versionRequired
                ? Result.Failure(UserErrors.VersionRequired)
                : Result.Success();
        }

        if (!RowVersion.TryParse(expectedVersion, out uint parsed))
        {
            // Malformed input, not a conflict: answering 409 here would tell the caller to retry a
            // request that can never succeed.
            return Result.Failure(UserErrors.MalformedVersion);
        }

        users.ExpectVersion(user, parsed);
        return Result.Success();
    }

    /// <summary>
    /// Reads the resource back after a successful write, so the response is byte-identical to what
    /// a following GET would return — including the *new* row version, which the caller needs for
    /// their next update.
    /// </summary>
    public static async Task<Result<UserDetail>> ReadBackAsync(
        IUserQueries userQueries,
        Guid userId,
        CancellationToken cancellationToken)
    {
        UserDetail? detail = await userQueries.GetAsync(userId, cancellationToken);

        return detail is null
            ? Result.Failure<UserDetail>(UserErrors.NotFound(userId))
            : Result.Success(detail);
    }
}
