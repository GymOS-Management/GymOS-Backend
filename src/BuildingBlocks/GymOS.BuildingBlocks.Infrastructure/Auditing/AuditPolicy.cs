using System.Text.RegularExpressions;

namespace GymOS.BuildingBlocks.Infrastructure.Auditing;

/// <summary>
/// What gets audited, and what must never appear in an audit row (AD-7).
///
/// Auditing is <b>opt-in per entity</b>: a table is audited because someone declared it,
/// not because it happened to be tracked. Redaction is the opposite — <b>opt-out</b>: any
/// property whose name looks like a secret is masked unless the declaration says otherwise.
/// The asymmetry is deliberate. Forgetting to declare an entity costs a missing trail;
/// forgetting to declare a secret writes a password hash into a table built to be read by
/// humans and retained for years. Only one of those is recoverable, so the default leans
/// the safe way — a field named <c>PasswordResetTokenHash</c> added years from now is
/// masked without anyone remembering to think about it.
/// </summary>
public sealed partial class AuditPolicy
{
    /// <summary>What a redacted value is replaced with — present, so the diff still shows the field changed.</summary>
    public const string RedactedMarker = "***REDACTED***";

    private readonly Dictionary<Type, AuditedEntity> _entities;

    public AuditPolicy(IEnumerable<AuditedEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        _entities = entities.ToDictionary(entity => entity.ClrType);
    }

    /// <summary>
    /// Property names that look like credentials or key material. Substring match,
    /// case-insensitive: <c>PasswordHash</c>, <c>TokenHash</c>, <c>SecurityStamp</c>,
    /// <c>ReplacedByTokenHash</c>, <c>ApiSecret</c>, <c>SigningKey</c> all match.
    /// </summary>
    [GeneratedRegex(
        "password|secret|token|hash|stamp|signingkey|apikey|credential|privatekey",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveName();

    public bool IsAudited(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return _entities.ContainsKey(clrType);
    }

    public AuditedEntity? For(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return _entities.TryGetValue(clrType, out AuditedEntity? entity) ? entity : null;
    }

    public bool ShouldRedact(Type clrType, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        AuditedEntity? entity = For(clrType);

        if (entity?.NeverRedacted.Contains(propertyName) == true)
        {
            return false;
        }

        return entity?.AlwaysRedacted.Contains(propertyName) == true || SensitiveName().IsMatch(propertyName);
    }
}

/// <param name="ClrType">The entity type.</param>
/// <param name="Name">
/// Stable name written to <c>entity_type</c> and used as the action prefix
/// (<c>user</c> → <c>user.created</c>). Decoupled from the CLR name so a class rename does
/// not rewrite history.
/// </param>
/// <param name="AlwaysRedacted">Sensitive properties the name heuristic would miss.</param>
/// <param name="NeverRedacted">
/// Escape hatch for properties the heuristic flags wrongly — a field genuinely named
/// something like <c>TokenCount</c>. Every entry here is a decision to log something that
/// looked sensitive, so it should be rare and obvious.
/// </param>
public sealed record AuditedEntity(
    Type ClrType,
    string Name,
    IReadOnlySet<string> AlwaysRedacted,
    IReadOnlySet<string> NeverRedacted)
{
    public static AuditedEntity Of<TEntity>(
        string name,
        IEnumerable<string>? alwaysRedacted = null,
        IEnumerable<string>? neverRedacted = null) =>
        new(
            typeof(TEntity),
            name,
            (alwaysRedacted ?? []).ToHashSet(StringComparer.Ordinal),
            (neverRedacted ?? []).ToHashSet(StringComparer.Ordinal));
}
