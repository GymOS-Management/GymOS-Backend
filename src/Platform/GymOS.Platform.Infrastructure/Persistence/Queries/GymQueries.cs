using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using RowVersion = GymOS.BuildingBlocks.Application.Abstractions.RowVersion;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

internal sealed class GymQueries : IGymQueries
{
    /// <summary>
    /// Sortable columns, by request value. An allowlist rather than dynamic expression building, for the
    /// reason given in <see cref="UserQueries"/>: <c>?sortBy=</c> is caller-controlled input.
    /// </summary>
    private static readonly string[] SortableFields = ["name", "slug", "status", "countryCode", "createdAtUtc"];

    private readonly AppDbContext _dbContext;
    private readonly IFeatureQueries _featureQueries;
    private readonly IPermissionEvaluator _permissionEvaluator;

    /// <summary>
    /// Resolution is delegated rather than reprojected here: <see cref="FeatureQueries"/> already owns
    /// the three-layer read, and a second copy of it in this file would be the one that drifts.
    ///
    /// <para><see cref="IPermissionEvaluator"/> is taken for the same reason as
    /// <see cref="IFeatureQueries"/>: <see cref="GetAsync"/> is the single point every route that
    /// produces a <see cref="GymDetail"/> — the direct <c>GET</c> and every write's read-back — routes
    /// through, so it is also the single point that must decide whether <c>features</c> comes back.
    /// Deciding it once here, rather than asking every caller to remember, is the fix for the same
    /// class of miss the M4.1 review found twice already (three renewal branches instead of five;
    /// <c>FeatureExceptionMaintainer</c> asking a question its callers already knew the answer to).
    /// </para>
    /// </summary>
    public GymQueries(AppDbContext dbContext, IFeatureQueries featureQueries, IPermissionEvaluator permissionEvaluator)
    {
        _dbContext = dbContext;
        _featureQueries = featureQueries;
        _permissionEvaluator = permissionEvaluator;
    }

    public async Task<PagedResult<GymListItem>> ListAsync(
        GymListFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        IQueryable<Gym> query = _dbContext.Set<Gym>().AsNoTracking();

        if (!filter.IncludeDeleted)
        {
            query = query.Where(gym => gym.Status != GymStatus.Deleted);
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            // An unparseable status yields no rows rather than an error: a stale filter chip in a
            // bookmarked URL should show an empty list, not a 400.
            if (!Enum.TryParse(filter.Status, ignoreCase: true, out GymStatus status))
            {
                return PagedResult.Empty<GymListItem>(filter.Page);
            }

            query = query.Where(gym => gym.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.CountryCode))
        {
            string countryCode = filter.CountryCode.Trim().ToUpperInvariant();
            query = query.Where(gym => gym.CountryCode == countryCode);
        }

        if (filter.Page.Search is { } search)
        {
            // Name, slug, and owner email: reception calls a gym by its name, an operator looking at a
            // support ticket has the URL or the email the owner wrote from.
            string pattern = SearchPatterns.Contains(search);
            query = query.Where(gym =>
                EF.Functions.ILike(gym.Name, pattern, SearchPatterns.EscapeCharacter) ||
                EF.Functions.ILike(gym.Slug, pattern, SearchPatterns.EscapeCharacter) ||
                EF.Functions.ILike(gym.OwnerEmail, pattern, SearchPatterns.EscapeCharacter));
        }

        int totalCount = await query.CountAsync(cancellationToken);

        // Projected to an anonymous type and mapped in memory: enum ToString() and positional-record
        // constructors do not translate to SQL.
        var page = await Sort(query, filter.Page)
            .Skip(filter.Page.Skip)
            .Take(filter.Page.PageSize)
            .Select(gym => new
            {
                gym.Id,
                gym.Slug,
                gym.Name,
                gym.Status,
                gym.CountryCode,
                gym.Currency,
                gym.OwnerName,
                gym.OwnerEmail,
                gym.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var items = page
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

        return new PagedResult<GymListItem>(items, filter.Page.Page, filter.Page.PageSize, totalCount);
    }

    public async Task<GymDetail?> GetAsync(Guid gymId, CancellationToken cancellationToken)
    {
        var projection = await _dbContext.Set<Gym>()
            .AsNoTracking()
            .Where(gym => gym.Id == gymId)
            .Select(gym => new
            {
                gym.Id,
                gym.Slug,
                gym.Name,
                gym.Status,
                gym.SuspensionReason,
                gym.OwnerName,
                gym.OwnerEmail,
                gym.Phone,
                gym.CountryCode,
                gym.TimeZoneId,
                gym.Currency,
                gym.DeletedAtUtc,
                gym.CreatedAtUtc,

                // PostgreSQL's own row version, mapped as a shadow concurrency token. Read here rather
                // than maintained by hand, so it cannot drift from what the store enforces.
                RowVersion = EF.Property<uint>(gym, "xmin"),
                Branding = _dbContext.Set<GymBranding>()
                    .Where(branding => branding.GymId == gym.Id)
                    .Select(branding => new
                    {
                        branding.LogoUrl,
                        branding.FaviconUrl,
                        branding.PrimaryColour,
                        branding.SecondaryColour,
                        branding.CustomDomain,
                        branding.EmailSenderName,
                    })
                    .FirstOrDefault(),

                // The gym's live subscription, if any (M3.2 â€” Â§3.3 always described the detail view as
                // carrying it). Projected here rather than fetched separately so the detail response
                // stays one round trip.
                Subscription = _dbContext.Set<GymSubscription>()
                    .Where(subscription => subscription.GymId == gym.Id
                        && (subscription.Status == SubscriptionStatus.Trialing
                            || subscription.Status == SubscriptionStatus.Active
                            || subscription.Status == SubscriptionStatus.PastDue))
                    .Select(subscription => new
                    {
                        subscription.Id,
                        subscription.PlanId,
                        subscription.Status,
                        subscription.BillingCycle,
                        Currency = subscription.Price.Currency,
                        Amount = subscription.Price.Amount,
                        subscription.CurrentPeriodEndUtc,
                        subscription.TrialEndsAtUtc,
                        subscription.CancelAtPeriodEnd,
                        PlanCode = _dbContext.Set<SubscriptionPlan>()
                            .Where(plan => plan.Id == subscription.PlanId)
                            .Select(plan => plan.Code)
                            .FirstOrDefault(),
                        PlanName = _dbContext.Set<SubscriptionPlan>()
                            .Where(plan => plan.Id == subscription.PlanId)
                            .Select(plan => plan.Name)
                            .FirstOrDefault(),
                    })
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (projection is null)
        {
            return null;
        }

        // Provisioning creates the branding row, so the fallback is for a gym that predates that
        // guarantee: an all-null branding block reads the same to a client as no branding set, which is
        // the truth, and avoids a null the contract does not allow.
        GymBrandingDetail branding = projection.Branding is null
            ? new GymBrandingDetail(null, null, null, null, null, null)
            : new GymBrandingDetail(
                projection.Branding.LogoUrl,
                projection.Branding.FaviconUrl,
                projection.Branding.PrimaryColour,
                projection.Branding.SecondaryColour,
                projection.Branding.CustomDomain,
                projection.Branding.EmailSenderName);

        GymSubscriptionSummary? subscription = projection.Subscription is null
            ? null
            : new GymSubscriptionSummary(
                projection.Subscription.Id,
                projection.Subscription.PlanId,
                projection.Subscription.PlanCode ?? string.Empty,
                projection.Subscription.PlanName ?? string.Empty,
                projection.Subscription.Status.ToString(),
                projection.Subscription.BillingCycle.ToString(),
                projection.Subscription.Currency.Code,
                projection.Subscription.Amount,
                projection.Subscription.CurrentPeriodEndUtc,
                projection.Subscription.TrialEndsAtUtc,
                projection.Subscription.CancelAtPeriodEnd);

        // Gated here, once, for every route that reaches this method — the direct GET and every
        // write's read-back alike (M4.1 task 4): one entry carries overrideReason, and a caller
        // without features.read gets neither it nor the rest of the block, resolved and discarded.
        // The same IPermissionEvaluator AuthorizationBehavior and the endpoint policies use, so this
        // can never disagree with them about who holds it.
        bool includeFeatures = await _permissionEvaluator.HasPermissionAsync(
            PlatformPermissions.FeaturesRead, cancellationToken);

        // A second round trip, deliberately: resolution reads the catalogue, the plan's opinions, and
        // the gym's overrides, and folding that into the projection above would duplicate the rule the
        // domain already owns.
        IReadOnlyList<ResolvedFeatureDetail>? features = includeFeatures
            ? await _featureQueries.ResolveForGymAsync(projection.Id, cancellationToken)
            : null;

        return new GymDetail(
            projection.Id,
            projection.Slug,
            projection.Name,
            projection.Status.ToString(),
            projection.SuspensionReason,
            projection.OwnerName,
            projection.OwnerEmail,
            projection.Phone,
            projection.CountryCode,
            projection.TimeZoneId,
            projection.Currency,
            projection.DeletedAtUtc,
            projection.CreatedAtUtc,
            branding,
            subscription,
            features,
            RowVersion.ToToken(projection.RowVersion));
    }

    public Task<bool> ExistsAsync(Guid gymId, CancellationToken cancellationToken) =>
        _dbContext.Set<Gym>().AsNoTracking().AnyAsync(gym => gym.Id == gymId, cancellationToken);

    private static IQueryable<Gym> Sort(IQueryable<Gym> query, PageRequest page) =>
        (page.SortBy is not null && SortableFields.Contains(page.SortBy, StringComparer.OrdinalIgnoreCase)
            ? page.SortBy.ToLowerInvariant() switch
            {
                "name" => page.Descending
                    ? query.OrderByDescending(gym => gym.Name)
                    : query.OrderBy(gym => gym.Name),
                "slug" => page.Descending
                    ? query.OrderByDescending(gym => gym.Slug)
                    : query.OrderBy(gym => gym.Slug),
                "status" => page.Descending
                    ? query.OrderByDescending(gym => gym.Status)
                    : query.OrderBy(gym => gym.Status),
                "countrycode" => page.Descending
                    ? query.OrderByDescending(gym => gym.CountryCode)
                    : query.OrderBy(gym => gym.CountryCode),
                _ => page.Descending
                    ? query.OrderByDescending(gym => gym.CreatedAtUtc)
                    : query.OrderBy(gym => gym.CreatedAtUtc),
            }
            // Newest first by default: the gym an operator wants is almost always one they just created.
            : query.OrderByDescending(gym => gym.CreatedAtUtc))
        // Id breaks ties, so paging cannot repeat or skip rows.
        .ThenBy(gym => gym.Id);
}
