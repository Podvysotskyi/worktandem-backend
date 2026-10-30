using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Worktandem.Organization.Application;
using Worktandem.Organization.Infrastructure;

namespace Worktandem.Organization.Api;

public interface IOrganizationAvailability { bool IsConfigured { get; } }
internal sealed record OrganizationAvailability(bool IsConfigured) : IOrganizationAvailability;

internal static class OrganizationSecurity
{
    public static void AddCompanySecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICompanyTransactions, PostgresCompanyTransactions>();
        services.AddScoped<CompanyWorkflow>();
        services.AddAuthorization(options => options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAssertion(_ => false).Build());
        services.AddSingleton<IAntiforgeryAdditionalDataProvider, OrganizationAntiforgeryData>();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-WT-CSRF";
            options.Cookie.Name = "__Host-wt-org-csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });
        var protection = services.AddDataProtection().SetApplicationName("Worktandem.Organization");
        var keys = configuration["Worktandem:DataProtection:KeysPath"];
        var keyCertificate = configuration["Worktandem:DataProtection:CertificatePath"];
        var clientCertificate = configuration["Worktandem:Identity:ClientCertificatePath"];
        var origin = configuration["Worktandem:Identity:Origin"];
        var configured = false;
        X509Certificate2? client = null;
        if (Uri.TryCreate(origin, UriKind.Absolute, out var endpoint) && endpoint.Scheme == "https" &&
            endpoint.AbsolutePath == "/" && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0 && endpoint.UserInfo.Length == 0 &&
            !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Organization")) &&
            !string.IsNullOrWhiteSpace(keys) && Path.IsPathFullyQualified(keys) && Directory.Exists(keys) &&
            !string.IsNullOrWhiteSpace(keyCertificate) && !string.IsNullOrWhiteSpace(clientCertificate))
        {
            X509Certificate2? key = null;
            try
            {
                key = X509CertificateLoader.LoadPkcs12FromFile(keyCertificate,
                    configuration["Worktandem:DataProtection:CertificatePassword"], X509KeyStorageFlags.EphemeralKeySet);
                client = X509CertificateLoader.LoadPkcs12FromFile(clientCertificate,
                    configuration["Worktandem:Identity:ClientCertificatePassword"], X509KeyStorageFlags.EphemeralKeySet);
                if (Usable(key) && Usable(client))
                {
                    protection.PersistKeysToFileSystem(new DirectoryInfo(keys)).ProtectKeysWithCertificate(key);
                    services.AddSingleton(key);
                    services.AddSingleton(client);
                    configured = true;
                }
                else { key.Dispose(); client.Dispose(); client = null; }
            }
            catch (Exception) { key?.Dispose(); client?.Dispose(); client = null; }
        }
        services.AddSingleton<IOrganizationAvailability>(new OrganizationAvailability(configured));
        services.AddHttpClient<IIdentityAuthority, IdentityAuthorityClient>(http =>
        {
            http.BaseAddress = Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri : new Uri("https://unconfigured.invalid");
            http.Timeout = TimeSpan.FromSeconds(5);
        }).ConfigurePrimaryHttpMessageHandler(() =>
        {
            // Standard server chain/hostname validation, no certificate callback or forwarded credentials.
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, CheckCertificateRevocationList = true };
            if (client is not null) handler.ClientCertificates.Add(client);
            return handler;
        });
        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(http => http.RemoveAllLoggers());
    }

    private static bool Usable(X509Certificate2 certificate) => certificate.HasPrivateKey &&
        certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow;

    public static ActorRequest Bind(HttpContext context)
    {
        var raw = context.Request.Cookies["__Host-wt-browser"];
        var selector = context.Request.Headers["X-WT-Account-Session"].ToString();
        if (raw is null || raw.Length != 43 || raw.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
            !Guid.TryParseExact(selector, "D", out var session) || session == Guid.Empty)
            throw new CompanyFailure("invalid_session", 401);
        byte[] bytes;
        try { bytes = WebEncoders.Base64UrlDecode(raw); }
        catch (FormatException) { throw new CompanyFailure("invalid_session", 401); }
        if (bytes.Length != 32) throw new CompanyFailure("invalid_session", 401);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, digest)], "BrowserContainer"));
        return new(raw, session);
    }

    public static async Task ValidateUnsafe(HttpContext context, IAntiforgery csrf)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var fetch = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if ((origin.Length > 0 && !string.Equals(origin, context.Request.Scheme + "://" + context.Request.Host, StringComparison.OrdinalIgnoreCase)) ||
            (fetch.Length > 0 && fetch is not "same-origin" and not "none"))
            throw new CompanyFailure("invalid_request_context", 400);
        try { await csrf.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { throw new CompanyFailure("invalid_antiforgery", 400); }
    }
}

internal sealed class OrganizationAntiforgeryData : IAntiforgeryAdditionalDataProvider
{
    public string GetAdditionalData(HttpContext context) => context.Request.Headers["X-WT-Account-Session"].ToString();
    public bool ValidateAdditionalData(HttpContext context, string additionalData) =>
        string.Equals(GetAdditionalData(context), additionalData, StringComparison.Ordinal);
}
