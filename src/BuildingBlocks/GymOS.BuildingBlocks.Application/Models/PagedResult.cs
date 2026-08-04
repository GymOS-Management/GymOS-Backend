namespace GymOS.BuildingBlocks.Application.Models;

/// <summary>
/// The list envelope every paged endpoint returns (ARCHITECTURE.md §3):
/// <c>{ items, page, pageSize, totalCount }</c>. One shape platform-wide, so a client's
/// table component is written once.
/// </summary>
public sealed record PagedResult<TItem>(
    IReadOnlyList<TItem> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Construction helpers for <see cref="PagedResult{TItem}"/>.</summary>
public static class PagedResult
{
    /// <summary>
    /// A page with no rows that still reports the request's paging, so a client's pager
    /// stays consistent when a filter matches nothing.
    /// </summary>
    public static PagedResult<TItem> Empty<TItem>(PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new PagedResult<TItem>([], request.Page, request.PageSize, 0);
    }
}

/// <summary>
/// Paging and sorting input, normalized on construction.
///
/// The cap is the important part: <see cref="PageSize"/> is clamped server-side, because a
/// client asking for a million rows is either a mistake or an attack, and either way the
/// database should not be asked. Out-of-range values are clamped rather than rejected —
/// paging is navigation, and a 400 on <c>?page=0</c> helps nobody.
/// </summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 200;

    public PageRequest(int? page = null, int? pageSize = null, string? sortBy = null, string? sortDirection = null, string? search = null)
    {
        Page = Math.Max(1, page ?? 1);
        PageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        SortBy = string.IsNullOrWhiteSpace(sortBy) ? null : sortBy.Trim();
        Descending = string.Equals(sortDirection?.Trim(), "desc", StringComparison.OrdinalIgnoreCase);
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
    }

    public int Page { get; }

    public int PageSize { get; }

    /// <summary>
    /// Requested sort field. Adapters map it through an explicit allowlist — never into a
    /// dynamic query expression, which would be an injection vector on a sort parameter.
    /// </summary>
    public string? SortBy { get; }

    public bool Descending { get; }

    public string? Search { get; }

    public int Skip => (Page - 1) * PageSize;
}
