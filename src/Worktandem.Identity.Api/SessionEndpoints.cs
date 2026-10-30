using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Worktandem.Identity.Application;

namespace Worktandem.Identity.Api;

internal static class SessionEndpoints
{
    private sealed record BootstrapRequest(Guid PendingLoginId, string Secret);
    private sealed record CompleteRequest(Guid PendingLoginId, string? DisplayName);
    private sealed record GrantRequest(Guid TargetAccountId, string Capability, bool Active, Guid? ExpectedVersion);

    public static void MapAccountSessions(this WebApplication app)
    {
        app.MapPost("/account/company-operation", async (HttpContext context, IAntiforgery antiforgery, AuthenticationWorkflow workflow) =>
            await Unsafe(context, antiforgery, async () => Results.Json(new
            {
                operationId = await workflow.CompanyOperation(
                AuthHttp.Digest(context)!, Selected(context), context.TraceIdentifier, context.RequestAborted)
            })));

        app.MapPost("/platform/reviewer-grants", async (HttpContext context, IAntiforgery antiforgery, AuthenticationWorkflow workflow) =>
            await Unsafe(context, antiforgery, async () =>
            {
                var request = await Body<GrantRequest>(context);
                var version = await workflow.ReviewerGrant(AuthHttp.Digest(context)!, Selected(context), request.TargetAccountId,
                    request.Capability, request.Active, request.ExpectedVersion, context.Request.Headers["Idempotency-Key"].ToString(),
                    context.TraceIdentifier, context.RequestAborted);
                return Results.Json(new { version });
            }));
        app.MapGet("/platform/reviewer-grants/{targetId:guid}", (HttpContext context, Guid targetId, AuthenticationWorkflow workflow) =>
            Safe(context, async () => Results.Json(await workflow.ReviewerGrants(AuthHttp.Digest(context) ?? throw new AuthenticationFailure("invalid_session", 401),
                Selected(context), targetId, context.RequestAborted))));
        app.MapPost("/setup/complete", async (HttpContext context, IAntiforgery antiforgery, AuthenticationWorkflow workflow) =>
            await Unsafe(context, antiforgery, async () =>
            {
                var request = await Body<BootstrapRequest>(context);
                var source = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
                var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
                var identity = await workflow.Bootstrap(AuthHttp.Digest(context)!, request.PendingLoginId,
                    request.Secret, sourceHash, context.TraceIdentifier, context.RequestAborted);
                return Results.Json(identity, statusCode: 201);
            }));

        app.MapPost("/auth/complete", async (HttpContext context, IAntiforgery antiforgery, AuthenticationWorkflow workflow) =>
            await Unsafe(context, antiforgery, async () =>
            {
                var request = await Body<CompleteRequest>(context);
                return Results.Json(await workflow.Complete(AuthHttp.Digest(context)!, request.PendingLoginId,
                    request.DisplayName, context.TraceIdentifier, context.RequestAborted));
            }));

        app.MapGet("/session/context", async (HttpContext context, IAntiforgery antiforgery, AuthenticationWorkflow workflow) =>
            await Safe(context, async () =>
            {
                var selector = AuthHttp.Selector(context) ?? throw new AuthenticationFailure("invalid_selector", 400);
                var digest = AuthHttp.EnsureBrowser(context);
                SessionIdentity? selected = null;
                if (selector != "prelogin")
                    selected = await workflow.Resolve(digest, Guid.Parse(selector), context.RequestAborted);
                var sessions = await workflow.List(digest, context.RequestAborted);
                var token = antiforgery.GetAndStoreTokens(context);
                return Results.Json(new { sessions, selected, csrfToken = token.RequestToken });
            }));

        app.MapPost("/session/logout-current", async (HttpContext context, IAntiforgery antiforgery, AuthenticationWorkflow workflow) =>
            await Unsafe(context, antiforgery, async () =>
            {
                var selector = AuthHttp.Selector(context);
                if (selector is null or "prelogin") throw new AuthenticationFailure("account_selection_required", 400);
                await workflow.LogoutCurrent(AuthHttp.Digest(context)!, Guid.Parse(selector), context.TraceIdentifier, context.RequestAborted);
                return Results.NoContent();
            }));

        app.MapPost("/session/logout-container", async (HttpContext context, IAntiforgery antiforgery, AuthenticationWorkflow workflow) =>
            await Unsafe(context, antiforgery, async () =>
            {
                await workflow.LogoutContainer(AuthHttp.Digest(context)!, context.TraceIdentifier, context.RequestAborted);
                var options = new CookieOptions { Path = "/", Secure = true, HttpOnly = true };
                context.Response.Cookies.Delete(AuthHttp.BrowserCookie, options);
                context.Response.Cookies.Delete("__Host-wt-csrf", options);
                return Results.NoContent();
            }));
    }

    private static Guid Selected(HttpContext context) => AuthHttp.Selector(context) is { } selector && Guid.TryParse(selector, out var id)
        ? id : throw new AuthenticationFailure("account_selection_required", 400);

    private static async Task<IResult> Unsafe(HttpContext context, IAntiforgery antiforgery, Func<Task<IResult>> action)
    {
        if (!await AuthHttp.ValidateUnsafe(context, antiforgery)) return Results.Empty;
        return await Safe(context, action);
    }

    internal static async Task<T> Body<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new AuthenticationFailure("json_required", 415);
        if (context.Request.ContentLength > 4096) throw new AuthenticationFailure("request_too_large", 413);
        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = 4096;
        return await context.Request.ReadFromJsonAsync<T>(context.RequestAborted)
            ?? throw new AuthenticationFailure("invalid_request", 400);
    }

    internal static async Task<IResult> Safe(HttpContext context, Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AuthenticationFailure failure)
        {
            if (failure.Status == 429) context.Response.Headers.RetryAfter = "600";
            return Problem(context, failure.Status, failure.Code);
        }
        catch (System.Text.Json.JsonException) { return Problem(context, 400, "invalid_request"); }
        catch (BadHttpRequestException failure) { return Problem(context, failure.StatusCode, "invalid_request"); }
        catch (Exception failure) when (failure is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Problem(context, 503, "identity_unavailable");
        }
    }

    private static IResult Problem(HttpContext context, int status, string code) =>
        Results.Problem(statusCode: status, extensions: new Dictionary<string, object?>
        { ["code"] = code, ["correlationId"] = context.TraceIdentifier });
}
