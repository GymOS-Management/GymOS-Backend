using System.Globalization;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Domain.Gyms;

namespace GymOS.Platform.Application.UnitTests.Fakes;

/// <summary>
/// In-memory stand-ins for the gym seams, behavioral rather than mocks for the same reason as the
/// identity fakes: a test asserts on the state a handler produced, not on the calls it made.
/// </summary>
internal sealed class FakeGymRepository : IGymRepository
{
    private readonly List<Gym> _gyms = [];
    private readonly List<GymBranding> _branding = [];

    public IReadOnlyList<Gym> Gyms => _gyms;

    public IReadOnlyList<GymBranding> Branding => _branding;

    public Dictionary<Guid, uint> ExpectedVersions { get; } = [];

    /// <summary>Ids whose aggregate version was deliberately advanced.</summary>
    public HashSet<Guid> AggregateChanges { get; } = [];

    /// <summary>Set to make <see cref="ExpectVersion"/> behave as a stale-version write.</summary>
    public uint? ConflictingVersion { get; set; }

    public void Seed(Gym gym) => _gyms.Add(gym);

    public void SeedBranding(GymBranding branding) => _branding.Add(branding);

    public Task<Gym?> FindByIdAsync(Guid gymId, CancellationToken cancellationToken) =>
        Task.FromResult(_gyms.SingleOrDefault(gym => gym.Id == gymId));

    public Task<Gym?> FindBySlugAsync(string slug, CancellationToken cancellationToken) =>
        Task.FromResult(_gyms.SingleOrDefault(gym =>
            string.Equals(gym.Slug, slug, StringComparison.OrdinalIgnoreCase)));

    public Task<GymBranding?> FindBrandingAsync(Guid gymId, CancellationToken cancellationToken) =>
        Task.FromResult(_branding.SingleOrDefault(branding => branding.GymId == gymId));

    public Task<bool> CustomDomainTakenAsync(
        string customDomain,
        Guid exceptGymId,
        CancellationToken cancellationToken) =>
        Task.FromResult(_branding.Any(branding =>
            branding.GymId != exceptGymId &&
            string.Equals(branding.CustomDomain, customDomain, StringComparison.OrdinalIgnoreCase)));

    public void Add(Gym gym) => _gyms.Add(gym);

    public void AddBranding(GymBranding branding) => _branding.Add(branding);

    /// <summary>
    /// Records the expectation and, when <see cref="ConflictingVersion"/> matches it, simulates the store
    /// refusing the write. The real check happens in PostgreSQL, so this is the only way a handler test
    /// can cover the conflict path at all.
    /// </summary>
    public void ExpectVersion(Gym gym, uint expectedVersion)
    {
        ExpectedVersions[gym.Id] = expectedVersion;

        if (ConflictingVersion == expectedVersion)
        {
            throw new ConcurrencyConflictException();
        }
    }

    public void MarkAggregateChanged(Gym gym) => AggregateChanges.Add(gym.Id);
}

/// <summary>
/// Read-side fake, projecting from the same in-memory aggregates the write side mutates — so a handler
/// that returns the resource it just changed is checked against real state rather than a fixture that
/// could disagree with it.
/// </summary>
internal sealed class FakeGymQueries : IGymQueries
{
    private readonly FakeGymRepository _gyms;

    public FakeGymQueries(FakeGymRepository gyms) => _gyms = gyms;

    /// <summary>Settable, so a "version changed" assertion has something to see.</summary>
    public uint RowVersion { get; set; } = 1;

    public List<GymListFilter> ListCalls { get; } = [];

    public Task<PagedResult<GymListItem>> ListAsync(
        GymListFilter filter,
        CancellationToken cancellationToken)
    {
        ListCalls.Add(filter);

        IEnumerable<Gym> gyms = filter.IncludeDeleted
            ? _gyms.Gyms
            : _gyms.Gyms.Where(gym => !gym.IsDeleted);

        var items = gyms
            .Select(gym => new GymListItem(
                gym.Id,
                gym.Slug,
                gym.Name,
                gym.Status.ToString(),
                gym.CountryCode,
                gym.Currency,
                gym.OwnerName,
                gym.OwnerEmail,
                gym.CreatedAtUtc))
            .ToList();

        return Task.FromResult(
            new PagedResult<GymListItem>(items, filter.Page.Page, filter.Page.PageSize, items.Count));
    }

    public Task<GymDetail?> GetAsync(Guid gymId, CancellationToken cancellationToken)
    {
        Gym? gym = _gyms.Gyms.SingleOrDefault(candidate => candidate.Id == gymId);
        if (gym is null)
        {
            return Task.FromResult<GymDetail?>(null);
        }

        GymBranding? branding = _gyms.Branding.SingleOrDefault(candidate => candidate.GymId == gymId);

        return Task.FromResult<GymDetail?>(new GymDetail(
            gym.Id,
            gym.Slug,
            gym.Name,
            gym.Status.ToString(),
            gym.SuspensionReason,
            gym.OwnerName,
            gym.OwnerEmail,
            gym.Phone,
            gym.CountryCode,
            gym.TimeZoneId,
            gym.Currency,
            gym.DeletedAtUtc,
            gym.CreatedAtUtc,
            new GymBrandingDetail(
                branding?.LogoUrl,
                branding?.FaviconUrl,
                branding?.PrimaryColour,
                branding?.SecondaryColour,
                branding?.CustomDomain,
                branding?.EmailSenderName),
            // No subscription in the gym fakes: subscriptions have their own context and fakes, and a
            // gym is perfectly valid without one.
            null,
            RowVersion.ToString(CultureInfo.InvariantCulture)));
    }
}
