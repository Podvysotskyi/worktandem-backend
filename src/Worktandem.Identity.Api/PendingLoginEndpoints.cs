using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Worktandem.Identity.Application;
using Worktandem.Identity.Domain;

namespace Worktandem.Identity.Api;

internal static class PendingLoginEndpoints
{
    private sealed record StartRequest(string Purpose, string ReturnPath, bool RequireFreshMfa = false);

    public static void UsePendingGoogleLogin(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/auth") || context.Request.Path.StartsWithSegments("/setup") ||
                context.Request.Path.StartsWithSegments("/session") || context.Request.Path == GoogleAuthConfiguration.Callback)
            {
                AuthHttp.NoStore(context);
                if (!context.RequestServices.GetRequiredService<IGoogleAuthAvailability>().IsConfigured)
                {
                    await AuthHttp.Problem(context, 503, "google_login_unavailable"); return;
                }
                if (!context.Request.IsHttps)
                {
                    await AuthHttp.Problem(context, 400, "https_required"); return;
                }
                if (AuthHttp.Digest(context) is { } digest) AuthHttp.ContainerPrincipal(context, digest);
            }
            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();

        // Cookie/token initialization has no account or database mutation.
        app.MapGet("/auth/context", (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (AuthHttp.Selector(context) is null) return Results.BadRequest();
            AuthHttp.EnsureBrowser(context);
            var token = antiforgery.GetAndStoreTokens(context);
            return Results.Json(new { csrfToken = token.RequestToken });
        });

        app.MapPost("/auth/start", async (HttpContext context, IAntiforgery antiforgery, PendingLoginWorkflow workflow) =>
        {
            if (!await AuthHttp.ValidateUnsafe(context, antiforgery)) return;
            if (!context.Request.HasJsonContentType())
            {
                await AuthHttp.Problem(context, 415, "json_required"); return;
            }
            if (context.Request.ContentLength > 4096)
            {
                await AuthHttp.Problem(context, 413, "request_too_large"); return;
            }
            if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                size.MaxRequestBodySize = 4096;
            try
            {
                var request = await context.Request.ReadFromJsonAsync<StartRequest>(context.RequestAborted);
                if (request is null || !Enum.TryParse<LoginPurpose>(request.Purpose, out var purpose) || !Enum.IsDefined(purpose))
                    throw new LoginFlowException("invalid_login_request");
                var digest = AuthHttp.Digest(context)!;
                var flow = await workflow.Begin(digest, purpose, request.ReturnPath, context.RequestAborted);
                var properties = new AuthenticationProperties { RedirectUri = "/auth/continue" };
                properties.Items["wt.flow"] = flow.ToString("D");
                properties.Items["wt.browser"] = digest;
                properties.Items["wt.purpose"] = purpose.ToString();
                if (request.RequireFreshMfa) properties.Items["wt.fresh-assurance"] = "true";
                await context.ChallengeAsync(GoogleAuthConfiguration.Scheme, properties);
            }
            catch (LoginFlowException failure)
            {
                var status = failure.Code switch
                {
                    "identity_unavailable" => 503,
                    "too_many_login_flows" => 429,
                    "setup_required" or "setup_unavailable" => 409,
                    _ => 400
                };
                if (status == 429) context.Response.Headers.RetryAfter = "300";
                await AuthHttp.Problem(context, status, failure.Code);
            }
            catch (System.Text.Json.JsonException) { await AuthHttp.Problem(context, 400, "invalid_login_request"); }
            catch (BadHttpRequestException failure) { await AuthHttp.Problem(context, failure.StatusCode, "invalid_login_request"); }
            catch (System.Data.Common.DbException) { await AuthHttp.Problem(context, 503, "identity_unavailable"); }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException) { await AuthHttp.Problem(context, 503, "identity_unavailable"); }
        });

        app.MapGet("/auth/pending/{id:guid}", async (Guid id, HttpContext context, PendingLoginWorkflow workflow) =>
        {
            var digest = AuthHttp.Digest(context);
            if (digest is null) return Results.NotFound();
            try
            {
                var summary = await workflow.Read(id, digest, context.RequestAborted);
                return summary is null ? Results.NotFound() : Results.Json(summary);
            }
            catch (System.Data.Common.DbException)
            {
                return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?>
                { ["code"] = "identity_unavailable", ["correlationId"] = context.TraceIdentifier });
            }
        });
    }
}
