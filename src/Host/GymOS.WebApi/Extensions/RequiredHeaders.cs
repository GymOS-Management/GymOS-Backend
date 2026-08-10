using Microsoft.OpenApi;

namespace GymOS.WebApi.Extensions;

/// <summary>
/// Declares, on the endpoint itself, the headers that endpoint's pipeline reads — so the OpenAPI
/// document says what the API actually does, including where a header is optional.
///
/// <para><b>Why markers rather than inference.</b> The three headers are read in three different
/// places: <c>Idempotency-Key</c> by middleware keyed on <c>IIdempotentCommand</c>, <c>If-Match</c>
/// by a <c>PreconditionHeaders.IfMatch</c> read inside the handler, and <c>X-Confirm-Action</c> by a
/// comparison inside the command. None of them is visible as endpoint metadata, so nothing can
/// derive the answer at document-generation time. Declaring it at the mapping — one call beside the
/// route it describes — is the honest version, and the R13 snapshot gate turns any omission into a
/// reviewed diff rather than a silent one.</para>
///
/// <para><b>Requiredness is not uniform for <c>If-Match</c>.</b> Whether it is enforced depends on
/// the command's own <c>ApplyExpectedVersion(..., versionRequired: ...)</c> call, which varies
/// deliberately: a <c>PUT</c> that overwrites the whole resource requires it, while an action
/// endpoint — mark-paid, void, a lifecycle transition — only honours it when supplied. Declaring an
/// honoured header as required would overstate enforcement, which is the same defect this task
/// exists to remove, just inverted; declaring a required header as optional would hide that a
/// missing precondition is refused. <see cref="RequiresIfMatch{TBuilder}"/> and
/// <see cref="HonoursIfMatch{TBuilder}"/> keep the two apart at the call site.</para>
/// </summary>
public static class RequiredHeaders
{
    /// <summary>Marker metadata; the name and requiredness carried here are what the operation declares.</summary>
    internal sealed record RequiredHeader(string Name, bool Required, string Description);

    public const string IdempotencyKey = "Idempotency-Key";
    public const string IfMatch = "If-Match";
    public const string ConfirmAction = "X-Confirm-Action";

    /// <summary>AD-39: the operation replays a stored outcome rather than repeating its effect.</summary>
    public static TBuilder RequiresIdempotencyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredHeader(
            IdempotencyKey,
            Required: true,
            "A caller-chosen key, one per intent (a UUID is recommended). Retrying with the same key "
            + "replays the stored outcome instead of repeating the effect. Omitting it is "
            + "400 `idempotency.key_required`."));

    /// <summary>
    /// Optimistic concurrency, enforced: the command's <c>ApplyExpectedVersion</c> call passes
    /// <c>versionRequired: true</c>, so a missing value is refused. Use for a <c>PUT</c> that
    /// overwrites the whole resource, where a lost update would silently discard a concurrent edit.
    /// </summary>
    public static TBuilder RequiresIfMatch<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredHeader(
            IfMatch,
            Required: true,
            "The strong ETag from the resource you read. Omitting it is 428; a stale value is 409."));

    /// <summary>
    /// Optimistic concurrency, optional: the command's <c>ApplyExpectedVersion</c> call passes
    /// <c>versionRequired: false</c>, so the header is honoured when supplied but a missing value is
    /// accepted as last-write-wins. Use for action endpoints — mark-paid, void, a lifecycle
    /// transition — where the operation's own state machine already refuses the unsafe orderings.
    /// </summary>
    public static TBuilder HonoursIfMatch<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredHeader(
            IfMatch,
            Required: false,
            "Optional. When supplied, the write is refused with 409 if the resource has changed "
            + "since you read it. Omitting it accepts last-write-wins."));

    /// <summary>AD-6 step-up: a value the caller can only know by having read the resource.</summary>
    public static TBuilder RequiresConfirmAction<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredHeader(
            ConfirmAction,
            Required: true,
            "Step-up confirmation, echoing the resource's own identifier (a gym's slug, an invoice's "
            + "number). A header rather than a body field so it survives DELETE and cannot be replayed "
            + "by a script that never read the resource."));

    /// <summary>Turns whatever markers the endpoint carries into declared header parameters.</summary>
    internal static void Declare(OpenApiOperation operation, IEnumerable<object> endpointMetadata)
    {
        foreach (RequiredHeader header in endpointMetadata.OfType<RequiredHeader>())
        {
            operation.Parameters ??= [];

            operation.Parameters.Add(new OpenApiParameter
            {
                Name = header.Name,
                In = ParameterLocation.Header,
                Required = header.Required,
                Description = header.Description,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }
    }
}
