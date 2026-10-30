using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Worktandem.Identity.Application;
using Worktandem.Identity.Domain;

namespace Worktandem.Identity.Api;

internal static class GoogleProofCallback
{
    public static async Task Receive(TicketReceivedContext context)
    {
        // TicketReceived occurs after the framework validates the OIDC response, including nonce/correlation.
        context.HandleResponse();
        var http = context.HttpContext;
        try
        {
            var properties = context.Properties;
            var digest = AuthHttp.Digest(http);
            if (digest is null || properties is null ||
                !properties.Items.TryGetValue("wt.browser", out var startedBrowser) || digest != startedBrowser ||
                !properties.Items.TryGetValue("wt.flow", out var flowText) || !Guid.TryParseExact(flowText, "D", out var flowId) ||
                context.Principal?.Identity?.IsAuthenticated != true)
                throw new LoginFlowException("invalid_login_flow");
            var principal = context.Principal;
            properties.Items.TryGetValue("wt.validated-issuer", out var issuer);
            if (issuer is not "https://accounts.google.com" and not "accounts.google.com")
                throw new LoginFlowException("invalid_provider_evidence");
            var subject = Single(principal, "sub") ?? throw new LoginFlowException("invalid_provider_evidence");
            var authTimeText = Single(principal, "auth_time");
            DateTimeOffset? authTime = null;
            if (authTimeText is not null)
            {
                if (!long.TryParse(authTimeText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    throw new LoginFlowException("invalid_provider_evidence");
                authTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            var evidence = new GoogleLoginEvidence("https://accounts.google.com", subject, Single(principal, "email"),
                Single(principal, "email_verified") == "true", Single(principal, "hd"), authTime,
                principal.FindAll("amr").Any(c => HasMfa(c.Value)));
            var workflow = http.RequestServices.GetRequiredService<PendingLoginWorkflow>();
            var returnPath = await workflow.Validate(flowId, digest, evidence, http.RequestAborted);
            // Identifier is non-bearer, browser-bound and short-lived. No secret or provider token is in the URL.
            http.Response.Redirect(returnPath + "?pendingLogin=" + flowId.ToString("D"));
        }
        catch (Exception failure)
        {
            // Never serialize/log the principal, token response, code, state or provider exception.
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Worktandem.Identity.Login")
                .LogWarning("Google proof rejected ({Category})",
                    failure is LoginFlowException flow ? flow.Code : failure.GetType().Name);
            var unavailable = failure is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException;
            await AuthHttp.Problem(http, unavailable ? 503 : 400, unavailable ? "identity_unavailable" : "invalid_google_login");
        }
    }

    private static string? Single(ClaimsPrincipal principal, string type)
    {
        var values = principal.FindAll(type).ToArray();
        if (values.Length > 1) throw new LoginFlowException("invalid_provider_evidence");
        return values.SingleOrDefault()?.Value;
    }

    private static bool HasMfa(string value)
    {
        if (value == "mfa") return true;
        if (!value.StartsWith('[')) return false;
        using var document = JsonDocument.Parse(value);
        return document.RootElement.ValueKind == JsonValueKind.Array &&
            document.RootElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "mfa");
    }
}
