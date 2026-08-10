using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Subscriptions;

/// <summary>Failure vocabulary of the subscription surface. Codes are stable API.</summary>
public static class SubscriptionErrors
{
    public static Error NotFound(Guid gymId) => Error.NotFound(
        "subscriptions.not_found",
        $"Gym '{gymId}' has no active subscription.");

    public static Error GymDeleted => Error.Conflict(
        "subscriptions.gym_deleted",
        "This gym has been deleted and cannot be subscribed to a plan.");

    public static Error PlanInactive(string code) => Error.Conflict(
        "subscriptions.plan_inactive",
        $"Plan '{code}' has been withdrawn and cannot be newly assigned. Gyms already on it keep it.");

    public static Error PriceNotOffered(string cycle, string currency) => Error.Validation(
        "subscriptions.price_not_offered",
        $"This plan has no {cycle} price in {currency}. Add one to the plan, or choose terms it offers.");

    public static Error AlreadyScheduledForCancellation => Error.Conflict(
        "subscriptions.cancellation_already_scheduled",
        "Cancellation is already scheduled for the end of the current period.");

    public static Error NoCancellationScheduled => Error.Conflict(
        "subscriptions.no_cancellation_scheduled",
        "No cancellation is scheduled, so there is nothing to withdraw.");

    public static Error NotTrialing => Error.Conflict(
        "subscriptions.not_trialing",
        "Only a subscription in its trial can be converted.");

    public static Error NotPastDue => Error.Conflict(
        "subscriptions.not_past_due",
        "Only a past-due subscription can be marked as paid.");

    public static Error AlreadyPastDue => Error.Conflict(
        "subscriptions.already_past_due",
        "This subscription is already past due.");

    public static Error VersionRequired => Error.Conflict(
        "concurrency.version_required",
        "This update requires an If-Match header carrying the rowVersion you read. " +
        "Re-read the subscription and send its version.");

    public static Error MalformedVersion => Error.Validation(
        "concurrency.malformed_version",
        "The If-Match header is not a version token issued by this API. " +
        "Send the rowVersion from the resource, quoted, without a weak (W/) prefix.");
}
