using GymOS.SharedKernel;
using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Settings;

/// <summary>
/// One stored value for one declared setting (M7 spec §3). Rows exist only for values someone has
/// set; the catalog supplies everything else. <see cref="Value"/> and <see cref="ProtectedValue"/> are
/// separate columns so the audit interceptor can redact the second by name and record the first in
/// plain — "who changed the SMTP host from what to what" stays answerable while the password never is.
/// An aggregate with a row version because two administrators can edit the same key.
/// </summary>
public sealed class PlatformSetting : AggregateRoot, IAuditable
{
    public const int MaxKeyLength = 100;
    public const int MaxDescriptionLength = 500;

    private PlatformSetting(Guid id, SettingDefinition definition)
        : base(id)
    {
        Key = definition.Key;
        Category = definition.Category;
        ValueType = definition.ValueType;
        IsSensitive = definition.IsSensitive;
        Description = definition.Description.Length <= MaxDescriptionLength
            ? definition.Description
            : definition.Description[..MaxDescriptionLength];
    }

    private PlatformSetting()
    {
    }

    public string Key { get; private set; } = string.Empty;

    public SettingCategory Category { get; private set; }

    public SettingValueType ValueType { get; private set; }

    public bool IsSensitive { get; private set; }

    /// <summary>The value in the clear. Always null for a sensitive key.</summary>
    public string? Value { get; private set; }

    /// <summary>Data Protection ciphertext. Always null for a non-sensitive key.</summary>
    public string? ProtectedValue { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public Guid? UpdatedBy { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public static PlatformSetting Create(
        Guid id, SettingDefinition definition, string? plainValue, string? protectedValue, Guid? updatedBy)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var setting = new PlatformSetting(id, definition);
        setting.Set(plainValue, protectedValue, updatedBy);
        return setting;
    }

    public void Set(string? plainValue, string? protectedValue, Guid? updatedBy)
    {
        if ((plainValue is null) == (protectedValue is null))
        {
            throw new DomainException($"Setting '{Key}' must hold exactly one of a plain value or a protected value.");
        }

        if (IsSensitive && plainValue is not null)
        {
            throw new DomainException($"Setting '{Key}' is sensitive and cannot hold a plain value.");
        }

        if (!IsSensitive && protectedValue is not null)
        {
            throw new DomainException($"Setting '{Key}' is not sensitive and cannot hold a protected value.");
        }

        Value = plainValue;
        ProtectedValue = protectedValue;
        UpdatedBy = updatedBy;
    }
}
