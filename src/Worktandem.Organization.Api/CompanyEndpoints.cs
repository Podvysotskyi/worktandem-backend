using Microsoft.AspNetCore.Antiforgery;
using Worktandem.Organization.Application;

namespace Worktandem.Organization.Api;

internal static class CompanyEndpoints
{
    private sealed record SubmitRequest(Guid OperationId, CompanyForm Form);
    public static void MapCompanies(this WebApplication app)
    {
        app.MapGet("/companies/context", (HttpContext context, CompanyWorkflow workflow, IAntiforgery csrf) => Safe(context, false, async actor =>
        {
            var result = await workflow.Context(actor, context.RequestAborted);
            return Results.Json(new { result.Applications, result.Workspaces, csrfToken = csrf.GetAndStoreTokens(context).RequestToken });
        }));
        app.MapGet("/companies/applications/by-operation/{operationId:guid}", (HttpContext context, Guid operationId, CompanyWorkflow workflow) =>
            Safe(context, false, async actor => Result(context, await workflow.OwnApplication(actor, operationId, context.RequestAborted))));
        app.MapPost("/companies/applications", (HttpContext context, CompanyWorkflow workflow) => Safe(context, true, async actor =>
        {
            var body = await Body<SubmitRequest>(context);
            return Result(context, await workflow.Submit(actor, body.OperationId, body.Form, Key(context), context.TraceIdentifier, context.RequestAborted), 201);
        }));
        app.MapPost("/companies/{companyId:guid}/applications", (HttpContext context, Guid companyId, CompanyWorkflow workflow) => Safe(context, true,
            async actor => Result(context, await workflow.Resubmit(actor, companyId, Version(context), await Body<CompanyForm>(context),
                Key(context), context.TraceIdentifier, context.RequestAborted), 201)));
        app.MapGet("/platform/company-applications", (HttpContext context, CompanyWorkflow workflow, int offset = 0, int limit = 25) => Safe(context, false,
            async actor => Results.Json(await workflow.Queue(actor, offset, limit, context.RequestAborted))));
        app.MapGet("/platform/companies/{companyId:guid}/application", (HttpContext context, Guid companyId, CompanyWorkflow workflow) => Safe(context, false,
            async actor => Results.Json(await workflow.ReviewDetail(actor, companyId, context.RequestAborted))));
        app.MapPost("/platform/companies/{companyId:guid}/applications/{applicationId:guid}/decision",
            (HttpContext context, Guid companyId, Guid applicationId, CompanyWorkflow workflow) => Safe(context, true, async actor =>
                Result(context, await workflow.Decide(actor, companyId, applicationId, Version(context), await Body<ReviewCommand>(context),
                    Key(context), context.TraceIdentifier, context.RequestAborted))));
        app.MapGet("/companies/{companyId:guid}/workspace", (HttpContext context, Guid companyId, CompanyWorkflow workflow) => Safe(context, false, async actor =>
        {
            if (!Guid.TryParseExact(context.Request.Headers["X-WT-Company"].ToString(), "D", out var selected) || selected != companyId)
                throw new CompanyFailure("company_selection_required", 400);
            return Results.Json(await workflow.Workspace(actor, companyId, context.RequestAborted));
        }));
    }

    private static string Key(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
    private static Guid Version(HttpContext context)
    {
        var value = context.Request.Headers.IfMatch.ToString();
        if (value.Length != 38 || value[0] != '"' || value[^1] != '"' || !Guid.TryParseExact(value[1..^1], "D", out var id))
            throw new CompanyFailure("if_match_required", 428);
        return id;
    }
    private static IResult Result(HttpContext context, ApplicationView result, int status = 200)
    {
        context.Response.Headers.ETag = "\"" + result.CompanyVersion.ToString("D") + "\"";
        return Results.Json(result, statusCode: status);
    }
    private static async Task<T> Body<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new CompanyFailure("json_required", 415);
        if (context.Request.ContentLength > 16384) throw new CompanyFailure("request_too_large", 413);
        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = 16384;
        return await context.Request.ReadFromJsonAsync<T>(context.RequestAborted) ?? throw new CompanyFailure("invalid_request", 400);
    }
    private static async Task<IResult> Safe(HttpContext context, bool unsafeMethod, Func<ActorRequest, Task<IResult>> work)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        try
        {
            if (!context.Request.IsHttps || !context.RequestServices.GetRequiredService<IOrganizationAvailability>().IsConfigured)
                throw new CompanyFailure("organization_unavailable", 503);
            var actor = OrganizationSecurity.Bind(context);
            if (unsafeMethod) await OrganizationSecurity.ValidateUnsafe(context, context.RequestServices.GetRequiredService<IAntiforgery>());
            return await work(actor);
        }
        catch (CompanyFailure failure) { return Problem(context, failure.Status, failure.Code); }
        catch (System.Text.Json.JsonException) { return Problem(context, 400, "invalid_request"); }
        catch (BadHttpRequestException failure) { return Problem(context, failure.StatusCode, "invalid_request"); }
        catch (Exception failure) when (failure is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { return Problem(context, 503, "organization_unavailable"); }
    }
    private static IResult Problem(HttpContext context, int status, string code) => Results.Problem(statusCode: status,
        extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.TraceIdentifier });
}
