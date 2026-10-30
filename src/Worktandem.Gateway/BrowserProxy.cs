using System.Net;
using System.Net.Security;
using System.Text.RegularExpressions;

namespace Worktandem.Gateway;

// Transport only. Services remain the authentication, CSRF, capability and tenant authority.
public sealed partial class BrowserProxy(IHttpClientFactory clients, IConfiguration configuration)
{
    public const string ClientName = "BrowserServices";
    private const long MaximumBodyBytes = 32 * 1024;
    private static readonly HashSet<string> RequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "Content-Type", "Cookie", "Origin", "Sec-Fetch-Site",
        "Sec-Fetch-Mode", "Sec-Fetch-Dest", "X-WT-CSRF", "X-WT-Account-Session", "X-WT-Company",
        "If-Match", "Idempotency-Key"
    };
    private static readonly HashSet<string> ResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Set-Cookie", "ETag", "Retry-After", "Location"
    };

    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online
        }
    };

    public async Task Forward(HttpContext context)
    {
        NoStore(context);
        var route = Resolve(context.Request.Path.Value ?? "", context.Request.Method);
        if (route is null)
        {
            await Problem(context, 404, "route_not_found"); return;
        }
        if (!TryOrigin(configuration["Worktandem:Gateway:PublicOrigin"], out var publicOrigin) ||
            !TryOrigin(configuration[$"Worktandem:Gateway:{route.Value.Service}Origin"], out var upstream))
        {
            await Problem(context, 503, "gateway_unavailable"); return;
        }
        // Fixed public origin, never caller-selected Host or forwarded identity/certificate headers.
        if (!context.Request.IsHttps || !string.Equals(context.Request.Host.Value, publicOrigin.Authority,
                StringComparison.OrdinalIgnoreCase))
        {
            await Problem(context, 400, "invalid_public_origin"); return;
        }
        if (context.Request.ContentLength > MaximumBodyBytes)
        {
            await Problem(context, 413, "request_too_large"); return;
        }
        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = MaximumBodyBytes;
        // Buffer a bounded body before sending any upstream request: malformed/oversized requests
        // cannot start a partial mutation, including when Content-Length is absent.
        using var body = new MemoryStream();
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var buffer = new byte[4096];
            try
            {
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    if (body.Length + read > MaximumBodyBytes)
                    { await Problem(context, 413, "request_too_large"); return; }
                    await body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                }
            }
            catch (BadHttpRequestException)
            { await Problem(context, 413, "request_too_large"); return; }
        }
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            new Uri(upstream, route.Value.Path + context.Request.QueryString.Value));
        // Host is also the TLS peer name on .NET. Certificates must cover the canonical public host;
        // the configured upstream origin controls connection routing, never certificate bypass.
        request.Headers.Host = publicOrigin.Authority;
        if (HttpMethods.IsPost(context.Request.Method)) request.Content = new ByteArrayContent(body.ToArray());
        var connectionTokens = context.Request.Headers.Connection.ToString().Split(',', StringSplitOptions.TrimEntries);
        foreach (var header in context.Request.Headers)
        {
            if (!RequestHeaders.Contains(header.Key) || connectionTokens.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // HttpClient logs are disabled at registration: callback queries contain codes/state.
            using var response = await clients.CreateClient(ClientName).SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.Headers.Location is { } location && !SafeRedirect(location))
            { await Problem(context, 502, "invalid_service_response"); return; }
            context.Response.StatusCode = (int)response.StatusCode;
            var responseConnectionTokens = response.Headers.Connection.ToArray();
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (ResponseHeaders.Contains(header.Key) && !responseConnectionTokens.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            await response.Content.CopyToAsync(context.Response.Body, deadline.Token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { context.Abort(); }
        catch (OperationCanceledException)
        {
            if (!context.Response.HasStarted) await Problem(context, 504, "service_timeout");
            else context.Abort();
        }
        catch (HttpRequestException)
        {
            if (!context.Response.HasStarted) await Problem(context, 502, "service_unavailable");
            else context.Abort();
        }
        catch (IOException)
        {
            if (!context.Response.HasStarted) await Problem(context, 502, "service_unavailable");
            else context.Abort();
        }
    }

    private static bool SafeRedirect(Uri location)
    {
        if (location.IsAbsoluteUri)
            return location.Scheme == "https" && location.Host == "accounts.google.com" &&
                location.IsDefaultPort && location.UserInfo.Length == 0;
        var value = location.OriginalString;
        var path = value.Split('?')[0];
        return path is "/" or "/setup" or "/auth/continue" &&
            !value.Contains('\\') && !value.Contains('#') && !value.Contains('\r') && !value.Contains('\n');
    }

    private static bool TryOrigin(string? value, out Uri origin)
    {
        origin = null!;
        return Uri.TryCreate(value, UriKind.Absolute, out origin!) && origin.Scheme == "https" &&
            origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0;
    }

    private static (string Service, string Path)? Resolve(string path, string method)
    {
        if (path == "/signin-google" && method == "GET") return ("Identity", path);
        if (path.StartsWith("/api/identity/", StringComparison.Ordinal))
        {
            var target = path[13..];
            if ((method == "GET" && (target is "/auth/context" or "/session/context" ||
                    IdentityRead().IsMatch(target))) ||
                (method == "POST" && target is "/auth/start" or "/auth/complete" or "/setup/complete" or
                    "/session/logout-current" or "/session/logout-container" or "/account/company-operation" or "/platform/reviewer-grants"))
                return ("Identity", target);
        }
        if (path.StartsWith("/api/organization/", StringComparison.Ordinal))
        {
            var target = path[17..];
            if ((method == "GET" && (target is "/companies/context" or "/platform/company-applications" || OrganizationRead().IsMatch(target))) ||
                (method == "POST" && (target == "/companies/applications" || OrganizationWrite().IsMatch(target))))
                return ("Organization", target);
        }
        return null;
    }

    [GeneratedRegex("^/(auth/pending|platform/reviewer-grants)/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityRead();
    [GeneratedRegex("^/(companies/applications/by-operation/[0-9a-fA-F-]{36}|companies/[0-9a-fA-F-]{36}/workspace|platform/companies/[0-9a-fA-F-]{36}/application)$", RegexOptions.CultureInvariant)]
    private static partial Regex OrganizationRead();
    [GeneratedRegex("^/(companies/[0-9a-fA-F-]{36}/applications|platform/companies/[0-9a-fA-F-]{36}/applications/[0-9a-fA-F-]{36}/decision)$", RegexOptions.CultureInvariant)]
    private static partial Regex OrganizationWrite();

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    private static Task Problem(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.Remove("Location");
        return context.Response.WriteAsJsonAsync(new { code, correlationId = context.TraceIdentifier }, context.RequestAborted);
    }
}
