using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Identity;

/// <summary>
/// One entry of the permission catalog (AD-5/AD-22). Rows are synced at startup
/// from each module's declared catalog — never created through the API. Keys are
/// explicit strings (<c>users.manage</c>); no wildcards, by decision.
/// </summary>
public sealed class Permission : Entity, IAuditable
{
    private Permission(Guid id, string key, string module, string description)
        : base(id)
    {
        Key = key;
        Module = module;
        Description = description;
    }

    private Permission()
    {
    }

    public string Key { get; private set; } = string.Empty;

    /// <summary>The module that owns (declared) this permission.</summary>
    public string Module { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public static Permission Create(string key, string module, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new Permission(Guid.NewGuid(), key, module, description);
    }

    /// <summary>Catalog sync may correct wording/ownership; the key itself is immutable.</summary>
    public void UpdateMetadata(string module, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Module = module;
        Description = description;
    }
}
