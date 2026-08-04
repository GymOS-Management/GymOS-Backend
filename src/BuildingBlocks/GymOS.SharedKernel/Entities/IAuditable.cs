namespace GymOS.SharedKernel.Entities;

/// <summary>
/// Row timestamps present on every persisted table (ARCHITECTURE.md §2). Set
/// exclusively by the persistence interceptor from <c>IClock</c> — domain and
/// application code never write these.
/// </summary>
public interface IAuditable
{
    DateTimeOffset CreatedAtUtc { get; set; }

    DateTimeOffset UpdatedAtUtc { get; set; }
}
