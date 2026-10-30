using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Worktandem.Identity.Application;
using Worktandem.Identity.Domain;
using Worktandem.Organization.Application;
using Worktandem.Organization.Domain;
using Xunit;
using Browser = Worktandem.PersistenceTests.BootstrapSessionTests.Browser;

namespace Worktandem.PersistenceTests;

[Collection("postgres")]
public sealed class CompanyWorkflowTests(PostgresFixture postgres)
{
    private static readonly CompanyForm Form = new("Example company", "UA", "A sufficiently detailed business description.", "Founder", "https://example.invalid");
    private sealed class Scenario(string identityConnection, string organizationConnection, GoogleTestHost identity,
        OrganizationTestHost organization, Browser ownerBrowser, SessionIdentity owner, Browser applicantBrowser,
        SessionIdentity applicant, Browser reviewerBrowser, SessionIdentity reviewer) : IAsyncDisposable
    {
        public string IdentityConnection => identityConnection;
        public string OrganizationConnection => organizationConnection;
        public GoogleTestHost Identity => identity;
        public OrganizationTestHost Organization => organization;
        public Browser OwnerBrowser => ownerBrowser;
        public Browser ApplicantBrowser => applicantBrowser;
        public Browser ReviewerBrowser => reviewerBrowser;
        public SessionIdentity Owner => owner;
        public SessionIdentity Applicant => applicant;
        public SessionIdentity Reviewer => reviewer;
        public async ValueTask DisposeAsync()
        {
            ownerBrowser.Dispose(); applicantBrowser.Dispose(); reviewerBrowser.Dispose();
            await organization.DisposeAsync(); await identity.DisposeAsync();
        }
    }
    private async Task<Scenario> Setup(bool grantReviewer = true)
    {
        var idConnection = await postgres.CreateDatabase(); var orgConnection = await postgres.CreateDatabase();
        await using (var db = PostgresFixture.Identity(idConnection)) await db.Database.MigrateAsync();
        await using (var db = PostgresFixture.Organization(orgConnection)) await db.Database.MigrateAsync();
        var identity = new GoogleTestHost(idConnection, enableCompletion: true);
        var ownerBrowser = new Browser(identity); var owner = await BootstrapSessionTests.Owner(ownerBrowser);
        var applicantBrowser = new Browser(identity); var applicant = await applicantBrowser.Complete(await applicantBrowser.Proof("applicant", "Register"));
        var reviewerBrowser = new Browser(identity); var reviewer = await reviewerBrowser.Complete(await reviewerBrowser.Proof("reviewer", "Register"));
        if (grantReviewer)
            foreach (var cap in new[] { "platform.companies.read", "platform.companies.review" })
            {
                using var grant = await Grant(ownerBrowser, owner, reviewer.AccountId, cap);
                Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
            }
        return new(idConnection, orgConnection, identity, new OrganizationTestHost(orgConnection, identity),
            ownerBrowser, owner, applicantBrowser, applicant, reviewerBrowser, reviewer);
    }

    private static async Task<HttpResponseMessage> Grant(Browser browser, SessionIdentity actor, Guid target,
        string cap, bool active = true, Guid? expected = null, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/platform/reviewer-grants")
        { Content = JsonContent.Create(new { targetAccountId = target, capability = cap, active, expectedVersion = expected }) };
        request.Headers.Add("X-WT-CSRF", await browser.Csrf(actor.AccountSessionId));
        request.Headers.Add("X-WT-Account-Session", actor.AccountSessionId.ToString());
        request.Headers.Add("Origin", "https://localhost");
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await browser.Client.SendAsync(request);
    }
    private static async Task<HttpResponseMessage> Get(HttpClient browser, Guid session, string path, Guid? company = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-WT-Account-Session", session.ToString());
        if (company is { } id) request.Headers.Add("X-WT-Company", id.ToString());
        return await browser.SendAsync(request);
    }
    private static async Task<string> Csrf(HttpClient browser, Guid session)
    {
        using var context = await Get(browser, session, "/companies/context");
        Assert.True(context.IsSuccessStatusCode, await context.Content.ReadAsStringAsync());
        return (await context.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrfToken").GetString()!;
    }
    private static async Task<HttpResponseMessage> Post(HttpClient browser, Guid session, string path, object body,
        string key, Guid? version = null, string? csrf = null)
    {
        csrf ??= await Csrf(browser, session);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-WT-Account-Session", session.ToString()); request.Headers.Add("X-WT-CSRF", csrf);
        request.Headers.Add("Idempotency-Key", key); request.Headers.Add("Origin", "https://localhost");
        if (version is { } id) request.Headers.TryAddWithoutValidation("If-Match", "\"" + id.ToString("D") + "\"");
        return await browser.SendAsync(request);
    }
    private static async Task<ApplicationView> Submit(Scenario s, HttpClient browser, string key = "initial")
    {
        using var response = await Post(browser, s.Applicant.AccountSessionId, "/companies/applications",
            new { operationId = s.Applicant.InitialRegistrationOperationId, form = Form }, key);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApplicationView>())!;
    }
    private static Task<HttpResponseMessage> Decide(Scenario s, HttpClient browser, ApplicationView application,
        ReviewOutcome outcome = ReviewOutcome.Approved, string key = "review", string? reason = null) =>
        Post(browser, s.Reviewer.AccountSessionId, $"/platform/companies/{application.CompanyId}/applications/{application.ApplicationId}/decision",
            new ReviewCommand(outcome, application.ApplicationVersion, reason, "Private reviewer note"), key, application.CompanyVersion);

    [Fact]
    public async Task Pending_review_reject_resubmit_approve_is_atomic_immutable_resumable_and_tenant_authorized()
    {
        await using var s = await Setup();
        using var applicant = s.Organization.Browser(s.ApplicantBrowser.Credential);
        using var reviewer = s.Organization.Browser(s.ReviewerBrowser.Credential);
        var pending = await Submit(s, applicant);
        using (var db = PostgresFixture.Organization(s.OrganizationConnection)) Assert.Empty(await db.Set<CompanyMembership>().ToListAsync());
        using var denied = await Get(applicant, s.Applicant.AccountSessionId, $"/companies/{pending.CompanyId}/workspace", pending.CompanyId);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var queue = await Get(reviewer, s.Reviewer.AccountSessionId, "/platform/company-applications?limit=10");
        Assert.Single((await queue.Content.ReadFromJsonAsync<ReviewView[]>())!);
        using var reject = await Decide(s, reviewer, pending, ReviewOutcome.Rejected, "reject", "Please clarify the business description.");
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var rejected = (await reject.Content.ReadFromJsonAsync<ApplicationView>())!;
        using var own = await Get(applicant, s.Applicant.AccountSessionId, "/companies/applications/by-operation/" + s.Applicant.InitialRegistrationOperationId);
        var ownJson = await own.Content.ReadAsStringAsync(); Assert.Contains("Please clarify", ownJson); Assert.DoesNotContain("Private reviewer note", ownJson);
        using var resubmit = await Post(applicant, s.Applicant.AccountSessionId, $"/companies/{pending.CompanyId}/applications", Form with
        { BusinessDescription = "A revised sufficiently detailed business description." }, "resubmit", rejected.CompanyVersion);
        Assert.Equal(HttpStatusCode.Created, resubmit.StatusCode);
        var current = (await resubmit.Content.ReadFromJsonAsync<ApplicationView>())!;
        Assert.Equal(2, current.SubmissionNumber); Assert.NotEqual(pending.ApplicationId, current.ApplicationId);
        using var superseded = await Post(reviewer, s.Reviewer.AccountSessionId,
            $"/platform/companies/{current.CompanyId}/applications/{pending.ApplicationId}/decision",
            new ReviewCommand(ReviewOutcome.Approved, pending.ApplicationVersion, null, null), "superseded", current.CompanyVersion);
        Assert.Equal(HttpStatusCode.Conflict, superseded.StatusCode);
        using var approve = await Decide(s, reviewer, current);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approved = (await approve.Content.ReadFromJsonAsync<ApplicationView>())!; Assert.Equal("Approved", approved.Status);
        using var workspace = await Get(applicant, s.Applicant.AccountSessionId, $"/companies/{current.CompanyId}/workspace", current.CompanyId);
        Assert.Equal(HttpStatusCode.OK, workspace.StatusCode);
        var space = (await workspace.Content.ReadFromJsonAsync<WorkspaceView>())!;
        Assert.Equal(s.Applicant.AccountId, space.AccountId); Assert.Contains(CompanyWorkflow.WorkspacePermission, space.Permissions);
        using var nonmember = await Get(reviewer, s.Reviewer.AccountSessionId, $"/companies/{current.CompanyId}/workspace", current.CompanyId);
        Assert.Equal(HttpStatusCode.NotFound, nonmember.StatusCode);
        await using var dbFinal = PostgresFixture.Organization(s.OrganizationConnection);
        Assert.Equal(2, await dbFinal.Set<CompanyApplication>().CountAsync()); Assert.Equal(2, await dbFinal.Set<ReviewDecision>().CountAsync());
        Assert.Single(await dbFinal.Set<CompanyMembership>().ToListAsync()); Assert.Equal(4, await dbFinal.Set<CompanyAudit>().CountAsync());
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => dbFinal.Database.ExecuteSqlRawAsync("UPDATE company_applications SET company_name='altered'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, immutable.SqlState);
        var auditImmutable = await Assert.ThrowsAsync<PostgresException>(() => dbFinal.Database.ExecuteSqlRawAsync("TRUNCATE company_audit"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, auditImmutable.SqlState);
    }

    [Fact]
    public async Task Concurrent_initial_submissions_and_lost_delivery_retries_keep_one_company_and_bound_payload()
    {
        await using var s = await Setup(); using var browser = s.Organization.Browser(s.ApplicantBrowser.Credential);
        var csrf = await Csrf(browser, s.Applicant.AccountSessionId);
        var requests = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Post(browser, s.Applicant.AccountSessionId,
            "/companies/applications", new { operationId = s.Applicant.InitialRegistrationOperationId, form = Form }, "submit-" + i, csrf: csrf)));
        var results = new List<ApplicationView>();
        foreach (var response in requests) { using (response) { Assert.Equal(HttpStatusCode.Created, response.StatusCode); results.Add((await response.Content.ReadFromJsonAsync<ApplicationView>())!); } }
        Assert.Single(results.Select(r => r.CompanyId).Distinct());
        using var changed = await Post(browser, s.Applicant.AccountSessionId, "/companies/applications",
            new { operationId = s.Applicant.InitialRegistrationOperationId, form = Form with { CompanyName = "Changed" } }, "submit-0");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var forged = await Post(browser, s.Applicant.AccountSessionId, "/companies/applications",
            new { operationId = Guid.CreateVersion7(), form = Form }, "forged"); Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);
        await using var db = PostgresFixture.Organization(s.OrganizationConnection);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM operation_receipts");
        var resumed = await Submit(s, browser, "after-retention"); Assert.Equal(results[0].CompanyId, resumed.CompanyId);
        Assert.Single(await db.Set<Company>().ToListAsync()); Assert.Single(await db.Set<CompanyApplication>().ToListAsync());
    }

    [Fact]
    public async Task Approval_races_replay_stale_versions_and_payload_conflicts_cannot_duplicate_activation()
    {
        await using var s = await Setup(); using var applicant = s.Organization.Browser(s.ApplicantBrowser.Credential);
        using var reviewer = s.Organization.Browser(s.ReviewerBrowser.Credential); var pending = await Submit(s, applicant);
        var csrf = await Csrf(reviewer, s.Reviewer.AccountSessionId);
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Post(reviewer, s.Reviewer.AccountSessionId,
            $"/platform/companies/{pending.CompanyId}/applications/{pending.ApplicationId}/decision",
            new ReviewCommand(ReviewOutcome.Approved, pending.ApplicationVersion, null, null), "same-decision", pending.CompanyVersion, csrf)));
        foreach (var response in responses) { using (response) Assert.Equal(HttpStatusCode.OK, response.StatusCode); }
        using var stale = await Decide(s, reviewer, pending, key: "different-key"); Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var payload = await Post(reviewer, s.Reviewer.AccountSessionId,
            $"/platform/companies/{pending.CompanyId}/applications/{pending.ApplicationId}/decision",
            new ReviewCommand(ReviewOutcome.Rejected, pending.ApplicationVersion, "different", null), "same-decision", pending.CompanyVersion);
        Assert.Equal(HttpStatusCode.Conflict, payload.StatusCode);
        await using var db = PostgresFixture.Organization(s.OrganizationConnection);
        Assert.Single(await db.Set<CompanyMembership>().ToListAsync()); Assert.Single(await db.Set<CompanyRoleAssignment>().ToListAsync());
        Assert.Single(await db.Set<ReviewDecision>().ToListAsync());
    }

    [Fact]
    public async Task Submission_and_approval_roll_back_every_local_effect_and_can_retry()
    {
        await using var s = await Setup(); using var applicant = s.Organization.Browser(s.ApplicantBrowser.Credential);
        using var reviewer = s.Organization.Browser(s.ReviewerBrowser.Credential);
        s.Organization.Rollback.Enabled = true;
        using var failed = await Post(applicant, s.Applicant.AccountSessionId, "/companies/applications",
            new { operationId = s.Applicant.InitialRegistrationOperationId, form = Form }, "retry");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        await using (var db = PostgresFixture.Organization(s.OrganizationConnection))
        { Assert.Empty(await db.Set<Company>().ToListAsync()); Assert.Empty(await db.Set<CompanyApplication>().ToListAsync()); }
        s.Organization.Rollback.Enabled = false; var pending = await Submit(s, applicant, "retry");
        var csrf = await Csrf(reviewer, s.Reviewer.AccountSessionId); s.Organization.Rollback.Enabled = true;
        using var approval = await Post(reviewer, s.Reviewer.AccountSessionId,
            $"/platform/companies/{pending.CompanyId}/applications/{pending.ApplicationId}/decision",
            new ReviewCommand(ReviewOutcome.Approved, pending.ApplicationVersion, null, null), "retry-review", pending.CompanyVersion, csrf);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, approval.StatusCode);
        await using (var db = PostgresFixture.Organization(s.OrganizationConnection))
        { Assert.Empty(await db.Set<CompanyMembership>().ToListAsync()); Assert.Empty(await db.Set<ReviewDecision>().ToListAsync()); Assert.Equal(CompanyStatus.Pending, (await db.Set<Company>().SingleAsync()).Status); }
        s.Organization.Rollback.Enabled = false;
        using var retry = await Decide(s, reviewer, pending, key: "retry-review"); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task Other_sessions_accounts_companies_and_forged_actor_headers_never_supply_authority()
    {
        await using var s = await Setup(); using var applicant = s.Organization.Browser(s.ApplicantBrowser.Credential);
        using var reviewer = s.Organization.Browser(s.ReviewerBrowser.Credential); var pending = await Submit(s, applicant);
        using var approve = await Decide(s, reviewer, pending); Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        using var foreignSession = await Get(applicant, s.Reviewer.AccountSessionId, "/companies/context"); Assert.Equal(HttpStatusCode.Unauthorized, foreignSession.StatusCode);
        reviewer.DefaultRequestHeaders.Add("X-Account-Id", s.Applicant.AccountId.ToString()); reviewer.DefaultRequestHeaders.Add("X-Role", "Owner");
        using var spoof = await Get(reviewer, s.Reviewer.AccountSessionId, $"/companies/{pending.CompanyId}/workspace", pending.CompanyId);
        Assert.Equal(HttpStatusCode.NotFound, spoof.StatusCode);
        using var operation = await Get(reviewer, s.Reviewer.AccountSessionId, "/companies/applications/by-operation/" + s.Applicant.InitialRegistrationOperationId);
        Assert.Equal(HttpStatusCode.NotFound, operation.StatusCode);
        using var crossCompany = await Get(applicant, s.Applicant.AccountSessionId, $"/companies/{pending.CompanyId}/workspace", Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.BadRequest, crossCompany.StatusCode);
        using var noCookie = s.Organization.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var anonymous = await Get(noCookie, s.Applicant.AccountSessionId, "/companies/context"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Theory]
    [InlineData("missing_capability", 403)]
    [InlineData("stale_mfa", 403)]
    [InlineData("assurance_unavailable", 403)]
    [InlineData("authority_unavailable", 503)]
    [InlineData("wrong_certificate", 403)]
    public async Task Review_fails_closed_on_authority_capability_assurance_and_transport(string mode, int status)
    {
        await using var s = await Setup(grantReviewer: mode != "missing_capability");
        using var applicant = s.Organization.Browser(s.ApplicantBrowser.Credential); var pending = await Submit(s, applicant);
        using var reviewer = s.Organization.Browser(s.ReviewerBrowser.Credential); var csrf = await Csrf(reviewer, s.Reviewer.AccountSessionId);
        if (mode == "stale_mfa") s.Identity.Clock.Advance(TimeSpan.FromMinutes(6));
        if (mode == "assurance_unavailable") s.Identity.Assurance.IsAvailable = false;
        if (mode == "authority_unavailable") s.Organization.AuthorityAvailable = false;
        if (mode == "wrong_certificate") s.Organization.WrongCertificate = true;
        using var response = await Post(reviewer, s.Reviewer.AccountSessionId,
            $"/platform/companies/{pending.CompanyId}/applications/{pending.ApplicationId}/decision",
            new ReviewCommand(ReviewOutcome.Approved, pending.ApplicationVersion, null, null), "denied", pending.CompanyVersion, csrf);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        await using var db = PostgresFixture.Organization(s.OrganizationConnection); Assert.Empty(await db.Set<CompanyMembership>().ToListAsync());
    }

    [Fact]
    public async Task Owner_optional_company_operation_is_stable_and_self_review_never_activates_owner()
    {
        await using var s = await Setup();
        using var operation = await s.OwnerBrowser.Post("/account/company-operation", new { }, await s.OwnerBrowser.Csrf(s.Owner.AccountSessionId), s.Owner.AccountSessionId);
        Assert.Equal(HttpStatusCode.OK, operation.StatusCode);
        var id = (await operation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("operationId").GetGuid();
        using var second = await s.OwnerBrowser.Post("/account/company-operation", new { }, await s.OwnerBrowser.Csrf(s.Owner.AccountSessionId), s.Owner.AccountSessionId);
        Assert.Equal(id, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("operationId").GetGuid());
        using var owner = s.Organization.Browser(s.OwnerBrowser.Credential);
        using var submitted = await Post(owner, s.Owner.AccountSessionId, "/companies/applications", new { operationId = id, form = Form }, "owner-company");
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode); var pending = (await submitted.Content.ReadFromJsonAsync<ApplicationView>())!;
        using var self = await Post(owner, s.Owner.AccountSessionId,
            $"/platform/companies/{pending.CompanyId}/applications/{pending.ApplicationId}/decision",
            new ReviewCommand(ReviewOutcome.Approved, pending.ApplicationVersion, null, null), "self", pending.CompanyVersion);
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using var reviewer = s.Organization.Browser(s.ReviewerBrowser.Credential);
        using var independent = await Decide(s, reviewer, pending); Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
    }

    [Fact]
    public async Task Reviewer_grants_are_explicit_versioned_audited_replayable_and_revoked_at_next_decision()
    {
        await using var s = await Setup(grantReviewer: false);
        using var forbidden = await Grant(s.ApplicantBrowser, s.Applicant, s.Reviewer.AccountId, "platform.companies.review"); Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var ownerPower = await Grant(s.OwnerBrowser, s.Owner, s.Reviewer.AccountId, "platform.owner"); Assert.Equal(HttpStatusCode.Forbidden, ownerPower.StatusCode);
        using var grant = await Grant(s.OwnerBrowser, s.Owner, s.Reviewer.AccountId, "platform.companies.review", key: "grant-review");
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode); var version = (await grant.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetGuid();
        using var replay = await Grant(s.OwnerBrowser, s.Owner, s.Reviewer.AccountId, "platform.companies.review", key: "grant-review");
        Assert.Equal(version, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetGuid());
        using var stale = await Grant(s.OwnerBrowser, s.Owner, s.Reviewer.AccountId, "platform.companies.review", false);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var revoke = await Grant(s.OwnerBrowser, s.Owner, s.Reviewer.AccountId, "platform.companies.review", false, version);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        using var applicant = s.Organization.Browser(s.ApplicantBrowser.Credential); var pending = await Submit(s, applicant);
        using var reviewer = s.Organization.Browser(s.ReviewerBrowser.Credential);
        using var denied = await Decide(s, reviewer, pending); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var db = PostgresFixture.Identity(s.IdentityConnection);
        var audits = await db.Set<AuthenticationAudit>().Where(a => a.TargetAccountId == s.Reviewer.AccountId).ToListAsync();
        Assert.Equal(2, audits.Count); Assert.All(audits, a => Assert.Equal("platform.companies.review", a.CapabilityKey));
    }

    [Fact]
    public async Task Two_accounts_in_one_browser_keep_independent_csrf_and_simultaneous_creation_contexts()
    {
        await using var s = await Setup();
        var second = await s.ApplicantBrowser.Complete(await s.ApplicantBrowser.Proof("second-applicant", "AddAccount"));
        using var browser = s.Organization.Browser(s.ApplicantBrowser.Credential);
        var aToken = await Csrf(browser, s.Applicant.AccountSessionId); var bToken = await Csrf(browser, second.AccountSessionId);
        using var wrong = await Post(browser, second.AccountSessionId, "/companies/applications",
            new { operationId = second.InitialRegistrationOperationId, form = Form }, "wrong-tab", csrf: aToken);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var responses = await Task.WhenAll(
            Post(browser, s.Applicant.AccountSessionId, "/companies/applications", new { operationId = s.Applicant.InitialRegistrationOperationId, form = Form }, "tab-a", csrf: aToken),
            Post(browser, second.AccountSessionId, "/companies/applications", new { operationId = second.InitialRegistrationOperationId, form = Form }, "tab-b", csrf: bToken));
        foreach (var response in responses) { using (response) Assert.Equal(HttpStatusCode.Created, response.StatusCode); }
        await using var db = PostgresFixture.Organization(s.OrganizationConnection);
        Assert.Equal(2, await db.Set<Company>().CountAsync());
        Assert.Equal(2, await db.Set<Company>().Select(c => c.CreatedByAccountId).Distinct().CountAsync());
    }

    [Fact]
    public async Task New_identity_routes_require_https_runtime_and_no_store_and_owner_grants_require_fresh_assurance()
    {
        await using var s = await Setup();
        using var view = await Get(s.OwnerBrowser.Client, s.Owner.AccountSessionId, "/platform/reviewer-grants/" + s.Reviewer.AccountId);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode); Assert.True(view.Headers.CacheControl!.NoStore);
        using var insecure = await s.OwnerBrowser.Client.GetAsync("http://localhost/platform/reviewer-grants/" + s.Reviewer.AccountId);
        Assert.Equal(HttpStatusCode.BadRequest, insecure.StatusCode); Assert.True(insecure.Headers.CacheControl!.NoStore);
        s.Identity.RuntimeAvailable = false;
        using var missing = await s.OwnerBrowser.Client.GetAsync("/platform/reviewer-grants/" + s.Reviewer.AccountId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode); Assert.True(missing.Headers.CacheControl!.NoStore);
        s.Identity.RuntimeAvailable = true; s.Identity.Clock.Advance(TimeSpan.FromMinutes(6));
        using var stale = await Grant(s.OwnerBrowser, s.Owner, s.Applicant.AccountId, "platform.companies.read");
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        using var org = s.Organization.Browser(s.ApplicantBrowser.Credential); s.Organization.Available = false;
        using var orgUnavailable = await Get(org, s.Applicant.AccountSessionId, "/companies/context");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, orgUnavailable.StatusCode); Assert.True(orgUnavailable.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("country")]
    [InlineData("description")]
    [InlineData("website")]
    public async Task Invalid_company_form_does_not_create_any_record(string invalid)
    {
        await using var s = await Setup(); using var browser = s.Organization.Browser(s.ApplicantBrowser.Credential);
        var form = invalid switch
        {
            "country" => Form with { CountryCode = "ZZ" },
            "description" => Form with { BusinessDescription = "short" },
            _ => Form with { Website = "javascript:alert(1)" }
        };
        using var response = await Post(browser, s.Applicant.AccountSessionId, "/companies/applications",
            new { operationId = s.Applicant.InitialRegistrationOperationId, form }, "invalid-form"); Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await using var db = PostgresFixture.Organization(s.OrganizationConnection); Assert.Empty(await db.Set<Company>().ToListAsync());
    }

    [Fact]
    public async Task Missing_rejection_reason_stale_submission_and_nonpending_company_cannot_activate_owner()
    {
        await using var s = await Setup(); using var applicant = s.Organization.Browser(s.ApplicantBrowser.Credential);
        using var reviewer = s.Organization.Browser(s.ReviewerBrowser.Credential); var pending = await Submit(s, applicant);
        using var noReason = await Decide(s, reviewer, pending, ReviewOutcome.Rejected, reason: "  "); Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        using var staleApplication = await Decide(s, reviewer, pending with { ApplicationVersion = Guid.CreateVersion7() }); Assert.Equal(HttpStatusCode.PreconditionFailed, staleApplication.StatusCode);
        await using var db = PostgresFixture.Organization(s.OrganizationConnection); var version = Guid.CreateVersion7();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET status='Suspended', version={version} WHERE id={pending.CompanyId}");
        using var unavailable = await Decide(s, reviewer, pending with { CompanyVersion = version }); Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        Assert.Empty(await db.Set<CompanyMembership>().ToListAsync()); Assert.Empty(await db.Set<ReviewDecision>().ToListAsync());
    }

    [Fact]
    public async Task Authority_that_ages_out_while_waiting_on_company_lock_cannot_approve()
    {
        await using var s = await Setup(); using var applicant = s.Organization.Browser(s.ApplicantBrowser.Credential);
        using var reviewer = s.Organization.Browser(s.ReviewerBrowser.Credential); var pending = await Submit(s, applicant);
        var csrf = await Csrf(reviewer, s.Reviewer.AccountSessionId);
        await using var blocker = PostgresFixture.Organization(s.OrganizationConnection); await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM companies WHERE id={pending.CompanyId} FOR UPDATE");
        var request = Post(reviewer, s.Reviewer.AccountSessionId,
            $"/platform/companies/{pending.CompanyId}/applications/{pending.ApplicationId}/decision",
            new ReviewCommand(ReviewOutcome.Approved, pending.ApplicationVersion, null, null), "blocked", pending.CompanyVersion, csrf);
        try { await BootstrapSessionTests.WaitForLock(s.OrganizationConnection, "companies"); s.Identity.Clock.Advance(TimeSpan.FromSeconds(11)); }
        finally { await tx.CommitAsync(); }
        using var denied = await request; Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        await using var db = PostgresFixture.Organization(s.OrganizationConnection); Assert.Empty(await db.Set<CompanyMembership>().ToListAsync());
        using var retry = await Decide(s, reviewer, pending); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task Competing_rejection_and_approval_have_one_terminal_winner()
    {
        await using var s = await Setup(); using var applicant = s.Organization.Browser(s.ApplicantBrowser.Credential);
        using var reviewer = s.Organization.Browser(s.ReviewerBrowser.Credential); var pending = await Submit(s, applicant);
        var csrf = await Csrf(reviewer, s.Reviewer.AccountSessionId);
        Task<HttpResponseMessage> Decision(ReviewOutcome outcome, string key) => Post(reviewer, s.Reviewer.AccountSessionId,
            $"/platform/companies/{pending.CompanyId}/applications/{pending.ApplicationId}/decision",
            new ReviewCommand(outcome, pending.ApplicationVersion, "Reason", null), key, pending.CompanyVersion, csrf);
        var responses = await Task.WhenAll(Decision(ReviewOutcome.Approved, "approve"), Decision(ReviewOutcome.Rejected, "reject"));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(responses, r => r.StatusCode == HttpStatusCode.PreconditionFailed);
        foreach (var response in responses) response.Dispose();
        await using var db = PostgresFixture.Organization(s.OrganizationConnection); var decision = await db.Set<ReviewDecision>().SingleAsync();
        Assert.Equal(decision.Outcome == ReviewOutcome.Approved ? 1 : 0, await db.Set<CompanyMembership>().CountAsync());
    }

    [Fact]
    public async Task Suspended_account_company_membership_or_removed_permission_denies_workspace_and_other_company_stays_usable()
    {
        await using var s = await Setup(); using var applicant = s.Organization.Browser(s.ApplicantBrowser.Credential);
        using var reviewer = s.Organization.Browser(s.ReviewerBrowser.Credential); var pending = await Submit(s, applicant);
        using var approve = await Decide(s, reviewer, pending); Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        await using var db = PostgresFixture.Organization(s.OrganizationConnection);
        var now = s.Identity.Clock.GetUtcNow(); var otherId = Guid.CreateVersion7(); var memberId = Guid.CreateVersion7(); var roleId = Guid.CreateVersion7();
        // Controlled fixture models an existing unrelated approved membership; no public bypass route exists.
        db.Add(new Company(otherId, "Existing company", "existing-" + otherId.ToString("N"), CompanyStatus.Approved, null,
            s.Reviewer.AccountId, Guid.CreateVersion7(), now, now, Guid.CreateVersion7()));
        db.Add(new CompanyMembership(memberId, otherId, s.Applicant.AccountId, MembershipStatus.Active, now, null, now, now, Guid.CreateVersion7()));
        db.Add(new CompanyRole(roleId, otherId, "Member", "MEMBER", true, now, now, Guid.CreateVersion7()));
        db.Add(new RolePermission(otherId, roleId, CompanyWorkflow.WorkspacePermission)); db.Add(new CompanyRoleAssignment(otherId, memberId, roleId, now)); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET status='Suspended', version={Guid.CreateVersion7()} WHERE id={pending.CompanyId}");
        using var companyDenied = await Get(applicant, s.Applicant.AccountSessionId, $"/companies/{pending.CompanyId}/workspace", pending.CompanyId); Assert.Equal(HttpStatusCode.NotFound, companyDenied.StatusCode);
        using var other = await Get(applicant, s.Applicant.AccountSessionId, $"/companies/{otherId}/workspace", otherId); Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE company_memberships SET status='Suspended', version={Guid.CreateVersion7()} WHERE id={memberId}");
        using var memberDenied = await Get(applicant, s.Applicant.AccountSessionId, $"/companies/{otherId}/workspace", otherId); Assert.Equal(HttpStatusCode.NotFound, memberDenied.StatusCode);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE company_memberships SET status='Active', version={Guid.CreateVersion7()} WHERE id={memberId}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM role_permissions WHERE company_id={otherId}");
        using var permissionDenied = await Get(applicant, s.Applicant.AccountSessionId, $"/companies/{otherId}/workspace", otherId); Assert.Equal(HttpStatusCode.NotFound, permissionDenied.StatusCode);
        await using var identityDb = PostgresFixture.Identity(s.IdentityConnection);
        await identityDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE accounts SET status='Suspended', version={Guid.CreateVersion7()} WHERE id={s.Applicant.AccountId}");
        using var accountDenied = await Get(applicant, s.Applicant.AccountSessionId, "/companies/context"); Assert.Equal(HttpStatusCode.Forbidden, accountDenied.StatusCode);
    }
}
