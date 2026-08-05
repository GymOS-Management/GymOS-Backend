using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Gyms;

/// <summary>Failure vocabulary of the gyms surface. Codes are stable API.</summary>
public static class GymErrors
{
    public static Error NotFound(Guid gymId) => Error.NotFound(
        "gyms.not_found",
        $"No gym with id '{gymId}' exists.");

    public static Error SlugTaken(string slug) => Error.Conflict(
        "gyms.slug_taken",
        $"The slug '{slug}' is already used by another gym. Slugs cannot be reused, including by a " +
        "deleted gym, because they appear in URLs members may still have.");

    public static Error CustomDomainTaken(string domain) => Error.Conflict(
        "gyms.custom_domain_taken",
        $"The domain '{domain}' is already claimed by another gym.");

    public static Error Deleted => Error.Conflict(
        "gyms.deleted",
        "This gym has been deleted and cannot be modified.");

    public static Error NotSuspended => Error.Conflict(
        "gyms.not_suspended",
        "Only a suspended gym can be reactivated.");

    public static Error AlreadySuspended => Error.Conflict(
        "gyms.already_suspended",
        "The gym is already suspended.");

    public static Error AlreadyActive => Error.Conflict(
        "gyms.already_active",
        "The gym is already active.");

    /// <summary>
    /// Step-up confirmation failed (AD-6). Deleting a gym removes a whole business's access, so the
    /// caller must name the gym they mean — a mis-clicked row in a list must not be able to do this.
    /// </summary>
    public static Error ConfirmationRequired(string slug) => Error.Validation(
        "gyms.confirmation_required",
        $"This action requires confirmation. Send the X-Confirm-Action header with the gym's slug " +
        $"('{slug}') to proceed.");

    public static Error ConfirmationMismatch => Error.Validation(
        "gyms.confirmation_mismatch",
        "The confirmation did not match this gym's slug. Nothing was changed.");

    public static Error VersionRequired => Error.Conflict(
        "concurrency.version_required",
        "This update requires an If-Match header carrying the rowVersion you read. " +
        "Re-read the gym and send its version.");

    public static Error MalformedVersion => Error.Validation(
        "concurrency.malformed_version",
        "The If-Match header is not a version token issued by this API. " +
        "Send the rowVersion from the resource, quoted, without a weak (W/) prefix.");
}
