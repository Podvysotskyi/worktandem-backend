using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Certificate;
using Worktandem.Identity.Application;

namespace Worktandem.Identity.Api;

internal static class InternalAuthority
{
    public const string Scheme = "InternalCertificate";

    public static void AddInternalAuthority(this IServiceCollection services)
    {
        services.AddAuthentication().AddCertificate(Scheme, options =>
        {
            // Default chain/EKU/validity/revocation validation remains enabled.
            options.AllowedCertificateTypes = CertificateTypes.Chained;
            options.Events = new CertificateAuthenticationEvents
            {
                OnCertificateValidated = context =>
                {
                    var pin = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()
                        ["Worktandem:Internal:OrganizationCertificateSha256"];
                    var actual = context.ClientCertificate.GetCertHashString(HashAlgorithmName.SHA256);
                    if (pin?.Length != 64 || !pin.All(char.IsAsciiHexDigit) ||
                        !string.Equals(pin, actual, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Fail("Service identity is unavailable."); return Task.CompletedTask;
                    }
                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("wt.service", "organization")], Scheme));
                    context.Success();
                    return Task.CompletedTask;
                }
            };
        });
    }

    private sealed record AuthorityRequest(string BrowserCredential, Guid AccountSessionId, string Action, Guid RequestId);

    public static void MapInternalAuthority(this WebApplication app)
    {
        app.MapPost("/internal/identity/validate-session", async (HttpContext context, AuthenticationWorkflow workflow) =>
            await SessionEndpoints.Safe(context, async () =>
            {
                AuthHttp.NoStore(context);
                if (!context.Request.IsHttps || context.Request.Headers.Origin.Count > 0)
                    throw new AuthenticationFailure("service_denied", 403);
                var caller = await context.AuthenticateAsync(Scheme);
                if (!caller.Succeeded || caller.Principal?.FindFirst("wt.service")?.Value != "organization")
                    throw new AuthenticationFailure("service_denied", 403);
                var request = await SessionEndpoints.Body<AuthorityRequest>(context);
                var digest = AuthHttp.CredentialDigest(request.BrowserCredential);
                if (digest is null) throw new AuthenticationFailure("invalid_session", 401);
                return Results.Json(await workflow.AuthorizeOrganization(digest, request.AccountSessionId,
                    request.Action, request.RequestId, context.TraceIdentifier, context.RequestAborted));
            }));
    }
}
