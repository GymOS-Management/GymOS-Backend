using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Domain.Gyms;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Gyms;

/// <summary>
/// The steps every gym-write handler shares: the expected-version precondition, the step-up confirmation,
/// and reading the resource back for the response.
///
/// Collected so a new gym endpoint cannot omit one. A handler that forgot the version check would
/// silently reintroduce last-write-wins on one route, and one that forgot the confirmation would let a
/// mis-clicked button delete a business.
/// </summary>
internal static class GymWrites
{
    /// <summary>
    /// Applies the caller's <c>If-Match</c> token.
    ///
    /// <paramref name="versionRequired"/> follows the platform convention: a PUT overwrites fields the
    /// caller read, so proceeding without a version *is* last-write-wins and is refused. An action
    /// endpoint (suspend, reactivate, activate, delete) changes state the transition table already
    /// guards, so a version is honoured when given but not demanded.
    /// </summary>
    public static Result ApplyExpectedVersion(
        IGymRepository gyms,
        Gym gym,
        string? expectedVersion,
        bool versionRequired)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return versionRequired ? Result.Failure(GymErrors.VersionRequired) : Result.Success();
        }

        if (!RowVersion.TryParse(expectedVersion, out uint parsed))
        {
            return Result.Failure(GymErrors.MalformedVersion);
        }

        gyms.ExpectVersion(gym, parsed);
        return Result.Success();
    }

    /// <summary>
    /// Verifies a step-up confirmation against the gym's slug (AD-6).
    ///
    /// <para>Checked here rather than at the endpoint because this is where the slug is known. An endpoint
    /// would have to load the gym to compare, which is the handler's job anyway — and a confirmation
    /// compared against something the caller also supplied would confirm nothing.</para>
    /// </summary>
    public static Result Confirm(Gym gym, string? confirmation)
    {
        if (string.IsNullOrWhiteSpace(confirmation))
        {
            return Result.Failure(GymErrors.ConfirmationRequired(gym.Slug));
        }

        return string.Equals(confirmation.Trim(), gym.Slug, StringComparison.Ordinal)
            ? Result.Success()
            : Result.Failure(GymErrors.ConfirmationMismatch);
    }

    /// <summary>
    /// Reads the gym back after a successful write, so the response matches what a following GET would
    /// return — including the *new* row version the caller needs for their next update.
    ///
    /// <para>Always includes the feature block: every write route this feeds is gated on
    /// <c>gyms.create</c>, <c>gyms.update</c>, or <c>gyms.suspend</c>, and today every stock role
    /// holding one of those also holds <c>features.read</c>. The narrower, permission-aware read
    /// (M4.1 task 4) belongs to <c>GetGymQueryHandler</c>, for the dedicated <c>GET /gyms/{id}</c>
    /// route the review actually flagged.</para>
    /// </summary>
    public static async Task<Result<GymDetail>> ReadBackAsync(
        IGymQueries gymQueries,
        Guid gymId,
        CancellationToken cancellationToken)
    {
        GymDetail? detail = await gymQueries.GetAsync(gymId, includeFeatures: true, cancellationToken);

        return detail is null
            ? Result.Failure<GymDetail>(GymErrors.NotFound(gymId))
            : Result.Success(detail);
    }
}
