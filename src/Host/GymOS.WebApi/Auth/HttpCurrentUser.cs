using System.Security.Claims;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Identity;

namespace GymOS.WebApi.Auth;

/// <summary>
/// The HTTP edge of <see cref="ICurrentUser"/>. Reads the validated token's claims — never
/// a request header or body, which the caller controls.
///
/// It degrades to "nobody" outside a request rather than throwing, which is what lets the
/// same implementation serve startup seeding and (later) the job runner: work with no
/// request context is genuinely system-initiated, and audit rows should say so.
/// </summary>
internal sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCurrentUser(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue(GymOSClaims.Subject), out Guid userId) && userId != Guid.Empty
            ? userId
            : null;

    public string? Email => Principal?.FindFirstValue(GymOSClaims.Email);

    public bool IsAuthenticated => UserId is not null;

    public string? IpAddress => _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => Truncate(_httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString(), 512);

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    /// <summary>
    /// The user-agent column is bounded; a caller can send a header far longer than that,
    /// and an oversized audit value must not be what fails the request being audited.
    /// </summary>
    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
