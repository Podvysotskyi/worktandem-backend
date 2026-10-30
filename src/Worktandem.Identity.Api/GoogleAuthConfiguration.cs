using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Worktandem.Identity.Application;

namespace Worktandem.Identity.Api;

public interface IGoogleAuthAvailability { bool IsConfigured { get; } }

internal sealed class GoogleAuthAvailability(bool configured) : IGoogleAuthAvailability
{
    public bool IsConfigured => configured;
}

internal static class GoogleAuthConfiguration
{
    public const string Scheme = "Google";
    public const string Callback = "/signin-google";
    public const string RejectScheme = "RejectInteractiveSignIn";

    public static void AddGoogleLogin(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IPendingLoginStore, Infrastructure.PostgresPendingLoginStore>();
        services.AddScoped<PendingLoginWorkflow>();
        // A browser container is never an authorized account. Later routes require explicit actor policies.
        services.AddAuthorization(options => options.DefaultPolicy =
            new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAssertion(_ => false).Build());
        services.AddSingleton<Microsoft.AspNetCore.Antiforgery.IAntiforgeryAdditionalDataProvider, ContainerAntiforgeryData>();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-WT-CSRF";
            options.Cookie.Name = "__Host-wt-csrf";
            options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        var clientId = configuration["Worktandem:Google:ClientId"];
        var clientSecret = configuration["Worktandem:Google:ClientSecret"];
        var keysPath = configuration["Worktandem:DataProtection:KeysPath"];
        var certificatePath = configuration["Worktandem:DataProtection:CertificatePath"];
        var certificatePassword = configuration["Worktandem:DataProtection:CertificatePassword"];
        var configured = false;
        var protection = services.AddDataProtection().SetApplicationName("Worktandem.Identity");
        // No generated deployment credentials or implicit machine key storage for enabled auth.
        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret) &&
            !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Identity")) &&
            !string.IsNullOrWhiteSpace(keysPath) && Path.IsPathFullyQualified(keysPath) && Directory.Exists(keysPath) &&
            !string.IsNullOrWhiteSpace(certificatePath))
        {
            try
            {
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword,
                    X509KeyStorageFlags.EphemeralKeySet);
                if (certificate.HasPrivateKey && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
                    certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow)
                {
                    protection.PersistKeysToFileSystem(new DirectoryInfo(keysPath)).ProtectKeysWithCertificate(certificate);
                    services.AddSingleton(certificate);
                    configured = true;
                }
                else certificate.Dispose();
            }
            catch (Exception) { /* Missing/unusable deployment configuration leaves auth unavailable. */ }
        }
        services.AddSingleton<IGoogleAuthAvailability>(new GoogleAuthAvailability(configured));
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, RejectInteractiveSignIn>(RejectScheme, _ => { })
            .AddOpenIdConnect(Scheme, options =>
            {
                options.Authority = "https://accounts.google.com";
                // A disabled scheme still needs valid option shape; endpoints reject missing configuration.
                var configuredClient = configuration["Worktandem:Google:ClientId"];
                options.ClientId = string.IsNullOrWhiteSpace(configuredClient) ? "not-configured" : configuredClient;
                options.ClientSecret = configuration["Worktandem:Google:ClientSecret"];
                options.SignInScheme = RejectScheme;
                options.CallbackPath = Callback;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(5);
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("email");
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidIssuers = ["https://accounts.google.com", "accounts.google.com"];
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.RequireSignedTokens = true;
                options.TokenValidationParameters.RequireExpirationTime = true;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                options.TokenValidationParameters.ValidAlgorithms = ["RS256"];
                options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
                options.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = context =>
                    {
                        // Retain validated issuer metadata in this server event context only.
                        // No persistence happens until TicketReceived, after nonce/correlation validation.
                        context.Properties!.Items["wt.validated-issuer"] = context.SecurityToken.Issuer;
                        return Task.CompletedTask;
                    },
                    OnRedirectToIdentityProvider = context =>
                    {
                        if (context.Properties.Items.TryGetValue("wt.purpose", out var purpose) && purpose == "Bootstrap")
                        {
                            context.ProtocolMessage.SetParameter("max_age", "300");
                            context.ProtocolMessage.SetParameter("claims", "{\"id_token\":{\"auth_time\":{\"essential\":true},\"amr\":{\"essential\":true}}}");
                        }
                        return Task.CompletedTask;
                    },
                    OnTicketReceived = GoogleProofCallback.Receive,
                    OnRemoteFailure = async context =>
                    {
                        context.HandleResponse();
                        await AuthHttp.Problem(context.HttpContext, 400, "invalid_google_login");
                    }
                };
            });
    }
}

// Receiving the OIDC ticket must go through PendingLogin. No framework auth cookie can accidentally create authority.
internal sealed class RejectInteractiveSignIn(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder), IAuthenticationSignInHandler
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    public Task SignInAsync(System.Security.Claims.ClaimsPrincipal user, AuthenticationProperties? properties) =>
        throw new InvalidOperationException("Interactive account sign-in is not enabled.");
    public Task SignOutAsync(AuthenticationProperties? properties) =>
        throw new InvalidOperationException("Interactive account sign-out is not enabled.");
}
