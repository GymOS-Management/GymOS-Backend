namespace GymOS.WebApi.Auth;

/// <summary>
/// The refresh token's transport (AD-4). It travels as an httpOnly cookie rather than
/// in a response body for one reason: no script the browser runs — including one
/// injected by an XSS flaw in a third-party bundle — can read it. That is a guarantee
/// no amount of careful client code can provide for a token held in JavaScript.
///
/// <c>SameSite=Strict</c> keeps it off cross-site requests entirely, and the narrow
/// <see cref="Path"/> means it is not attached to any request that has no business
/// seeing it.
/// </summary>
public static class RefreshTokenCookie
{
    public const string Name = "gymos_refresh_token";

    /// <summary>Only the auth endpoints ever need it — the rest of the API never receives it.</summary>
    public const string Path = "/api/v1/auth";

    public static void Write(HttpResponse response, string rawToken, DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Append(Name, rawToken, BuildOptions(expiresAtUtc));
    }

    /// <summary>
    /// Clears the cookie by overwriting it with an expired empty value. The options must
    /// match those it was written with, or the browser keeps the original alongside it.
    /// </summary>
    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Delete(Name, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = Path,
        });
    }

    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Cookies.TryGetValue(Name, out string? token) && !string.IsNullOrWhiteSpace(token)
            ? token
            : null;
    }

    private static CookieOptions BuildOptions(DateTimeOffset expiresAtUtc) => new()
    {
        HttpOnly = true,

        // Always Secure, including in development: browsers accept Secure cookies over
        // http://localhost, so there is no reason to make this environment-dependent —
        // and an environment-dependent security attribute is one deploy away from
        // shipping the wrong value.
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = expiresAtUtc,
        IsEssential = true,
    };
}
