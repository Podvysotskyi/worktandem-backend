using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace Worktandem.Identity.Api;

internal static class AuthHttp
{
    public const string BrowserCookie = "__Host-wt-browser";
    public const string SelectorHeader = "X-WT-Account-Session";

    public static string? Digest(HttpContext context)
    {
        var raw = context.Request.Cookies[BrowserCookie];
        return CredentialDigest(raw);
    }

    public static string? CredentialDigest(string? raw)
    {
        if (raw is null || raw.Length != 43 || raw.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return null;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(raw);
            return bytes.Length == 32 ? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() : null;
        }
        catch (FormatException) { return null; }
    }

    public static void ContainerPrincipal(HttpContext context, string digest) =>
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, digest)], "BrowserContainer"));

    public static string EnsureBrowser(HttpContext context)
    {
        var digest = Digest(context);
        if (digest is null)
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            var raw = WebEncoders.Base64UrlEncode(bytes);
            digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            context.Response.Cookies.Append(BrowserCookie, raw, new CookieOptions
            {
                Secure = true,
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = TimeSpan.FromHours(12),
                IsEssential = true
            });
        }
        ContainerPrincipal(context, digest);
        return digest;
    }

    public static string? Selector(HttpContext context)
    {
        var value = context.Request.Headers[SelectorHeader].ToString();
        return value.Length == 0 ? "prelogin" : Guid.TryParseExact(value, "D", out var id) ? id.ToString("D") : null;
    }

    public static async Task<bool> ValidateUnsafe(HttpContext context, IAntiforgery antiforgery)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if ((origin.Length > 0 && !string.Equals(origin, context.Request.Scheme + "://" + context.Request.Host, StringComparison.OrdinalIgnoreCase)) ||
            (fetchSite.Length > 0 && fetchSite is not "same-origin" and not "none") || Digest(context) is null || Selector(context) is null)
        {
            await Problem(context, 400, "invalid_request_context"); return false;
        }
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { await Problem(context, 400, "invalid_antiforgery"); return false; }
    }

    public static Task Problem(HttpContext context, int status, string code) =>
        Results.Problem(statusCode: status, extensions: new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = context.TraceIdentifier
        }).ExecuteAsync(context);

    public static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

internal sealed class ContainerAntiforgeryData : IAntiforgeryAdditionalDataProvider
{
    public string GetAdditionalData(HttpContext context) => AuthHttp.Selector(context) ?? "invalid";
    public bool ValidateAdditionalData(HttpContext context, string additionalData) =>
        AuthHttp.Selector(context) is { } selector && string.Equals(selector, additionalData, StringComparison.Ordinal);
}
