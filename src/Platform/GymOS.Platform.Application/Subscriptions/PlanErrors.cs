using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Subscriptions;

/// <summary>Failure vocabulary of the plan catalogue. Codes are stable API.</summary>
public static class PlanErrors
{
    public static Error NotFound(Guid planId) => Error.NotFound(
        "plans.not_found",
        $"No subscription plan with id '{planId}' exists.");

    public static Error CodeTaken(string code) => Error.Conflict(
        "plans.code_taken",
        $"The plan code '{code}' is already in use. Codes identify plans in exports and integrations, " +
        "so two plans cannot share one.");

    public static Error AlreadyActive => Error.Conflict(
        "plans.already_active",
        "The plan is already active.");

    public static Error AlreadyInactive => Error.Conflict(
        "plans.already_inactive",
        "The plan is already inactive.");

    public static Error PriceNotFound => Error.NotFound(
        "plans.price_not_found",
        "That price does not belong to this plan.");

    public static Error UnknownBillingCycle(string value) => Error.Validation(
        "plans.billing_cycle_invalid",
        $"'{value}' is not a billing cycle. Use Monthly, Quarterly, or Yearly.");

    public static Error InvalidCurrency(string value) => Error.Validation(
        "plans.currency_invalid",
        $"'{value}' is not a three-letter ISO 4217 currency code, such as EUR.");

    public static Error VersionRequired => Error.Conflict(
        "concurrency.version_required",
        "This update requires an If-Match header carrying the rowVersion you read. " +
        "Re-read the plan and send its version.");

    public static Error MalformedVersion => Error.Validation(
        "concurrency.malformed_version",
        "The If-Match header is not a version token issued by this API. " +
        "Send the rowVersion from the resource, quoted, without a weak (W/) prefix.");
}
