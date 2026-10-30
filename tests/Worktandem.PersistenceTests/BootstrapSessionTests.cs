using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Worktandem.Identity.Application;
using Worktandem.Identity.Domain;
using Worktandem.Identity.Infrastructure;
using Xunit;

namespace Worktandem.PersistenceTests;

[Collection("postgres")]
public sealed class BootstrapSessionTests(PostgresFixture postgres)
{
    internal sealed class Browser(GoogleTestHost host) : IDisposable
    {
        public HttpClient Client { get; } = host.Browser();
        public string Credential { get; private set; } = "";
        public async Task<string> Csrf(Guid? selector = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/context");
            if (selector is { } id) request.Headers.Add("X-WT-Account-Session", id.ToString());
            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var cookie in cookies.Where(c => c.StartsWith("__Host-wt-browser=", StringComparison.Ordinal)))
                    Credential = cookie.Split(';')[0].Split('=', 2)[1];
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrfToken").GetString()!;
        }

        public async Task<Guid> Proof(string subject, string purpose = "Bootstrap", GoogleSimulator.TokenCase? token = null)
        {
            var challenge = await BeginProof(purpose);
            return await FinishProof(challenge, host.Provider.IssueCode(challenge, token ?? new(Subject: subject)));
        }

        public async Task<Uri> BeginProof(string purpose, bool requireFreshMfa = false)
        {
            var csrf = await Csrf();
            using var start = await Post("/auth/start", new { purpose, returnPath = "/auth/continue", requireFreshMfa }, csrf);
            Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
            return start.Headers.Location!;
        }

        public async Task<Guid> FinishProof(Uri challenge, string code)
        {
            var state = QueryHelpers.ParseQuery(challenge.Query)["state"].ToString();
            using var callback = await Client.GetAsync(QueryHelpers.AddQueryString("/signin-google",
                new Dictionary<string, string?> { ["code"] = code, ["state"] = state }));
            Assert.True(callback.StatusCode == HttpStatusCode.Redirect, "Fixture provider proof must validate: " + host.OidcFailure);
            return Guid.Parse(QueryHelpers.ParseQuery(new Uri(Client.BaseAddress!, callback.Headers.Location!).Query)["pendingLogin"].ToString());
        }

        public async Task<HttpResponseMessage> Post(string path, object body, string csrf, Guid? selector = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-WT-CSRF", csrf);
            request.Headers.Add("Origin", "https://localhost");
            if (selector is { } id) request.Headers.Add("X-WT-Account-Session", id.ToString());
            return await Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> Bootstrap(Guid proof, string csrf, string secret = FixtureSecret.Value) =>
            Post("/setup/complete", new { pendingLoginId = proof, secret }, csrf);
        public async Task<SessionIdentity> Complete(Guid proof, string? name = "Fixture member")
        {
            using var response = await Post("/auth/complete", new { pendingLoginId = proof, displayName = name }, await Csrf());
            Assert.True(response.StatusCode == HttpStatusCode.OK, "Fixture account sign-in must succeed: " + await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<SessionIdentity>())!;
        }
        public async Task<HttpResponseMessage> Context(Guid? selector = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/session/context");
            if (selector is { } id) request.Headers.Add("X-WT-Account-Session", id.ToString());
            return await Client.SendAsync(request);
        }
        public async Task<HttpResponseMessage> Authority(SessionIdentity session, string action = "organization.account",
            string certificate = "organization", Guid? requestId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/identity/validate-session")
            {
                Content = JsonContent.Create(new
                {
                    browserCredential = Credential,
                    accountSessionId = session.AccountSessionId,
                    action,
                    requestId = requestId ?? Guid.CreateVersion7()
                })
            };
            if (certificate.Length > 0) request.Headers.Add("X-Fixture-Certificate", certificate);
            return await Client.SendAsync(request);
        }
        public void Dispose() => Client.Dispose();
    }

    private async Task<(string Connection, GoogleTestHost Host)> Host()
    {
        var connection = await postgres.CreateDatabase();
        await using var db = PostgresFixture.Identity(connection);
        await db.Database.MigrateAsync();
        return (connection, new GoogleTestHost(connection, enableCompletion: true));
    }

    internal static async Task<SessionIdentity> Owner(Browser browser, string subject = "owner")
    {
        var proof = await browser.Proof(subject);
        using var response = await browser.Bootstrap(proof, await browser.Csrf());
        Assert.True(response.StatusCode == HttpStatusCode.Created, "Fixture bootstrap must succeed: " + await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SessionIdentity>())!;
    }

    [Fact]
    public async Task First_owner_completion_is_atomic_consumed_audited_company_optional_and_permanently_closed()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host);
        var proof = await browser.Proof("owner");
        using var response = await browser.Bootstrap(proof, await browser.Csrf());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var identity = (await response.Content.ReadFromJsonAsync<SessionIdentity>())!;
        Assert.Null(identity.InitialRegistrationOperationId);
        Assert.Contains("platform.owner", identity.Capabilities);
        await using var db = PostgresFixture.Identity(connection);
        Assert.Single(await db.Set<Account>().ToListAsync());
        Assert.Single(await db.Set<ExternalIdentity>().ToListAsync());
        Assert.Single(await db.Set<AccountSession>().ToListAsync());
        Assert.Equal(6, await db.Set<PlatformGrant>().CountAsync());
        Assert.NotNull((await db.Set<PendingLogin>().SingleAsync()).ConsumedAt);
        Assert.Equal(SetupStatus.Completed, (await db.Set<SetupState>().SingleAsync()).Status);
        Assert.Single(await db.Set<AuthenticationAudit>().Where(a => a.EventName == "bootstrap.completed").ToListAsync());
        using var consumed = await browser.Client.GetAsync("/auth/pending/" + proof);
        Assert.Equal(HttpStatusCode.NotFound, consumed.StatusCode);
        using var replay = await browser.Bootstrap(proof, await browser.Csrf());
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        host.SetupSecret.Enabled = false;
        var normalProof = await browser.Proof("second-google", "Register");
        var normal = await browser.Complete(normalProof);
        Assert.NotNull(normal.InitialRegistrationOperationId);
        Assert.NotEqual(identity.AccountId, normal.AccountId);
        Assert.Empty(normal.Capabilities);
        using var closed = await browser.Bootstrap(proof, await browser.Csrf());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, closed.StatusCode);
        var auditError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE authentication_audit SET event_name='session.created'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, auditError.SqlState);
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains(FixtureSecret.Value, StringComparison.Ordinal) ||
            message.Contains(browser.Credential, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("wrong_secret", 409)]
    [InlineData("missing_secret", 503)]
    [InlineData("missing_mfa", 409)]
    [InlineData("missing_auth_time", 409)]
    [InlineData("stale_mfa", 409)]
    [InlineData("unverified_deployment", 503)]
    [InlineData("expired_proof", 409)]
    public async Task Invalid_setup_never_creates_accounts_grants_or_sessions(string failure, int expected)
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host);
        var token = failure switch
        {
            "missing_mfa" => new GoogleSimulator.TokenCase(Mfa: false),
            "missing_auth_time" => new GoogleSimulator.TokenCase(MissingAuthTime: true),
            "stale_mfa" => new GoogleSimulator.TokenCase(StaleAuthTime: true),
            _ => new GoogleSimulator.TokenCase()
        };
        var proof = await browser.Proof("fixture-subject", token: token);
        if (failure == "missing_secret") host.SetupSecret.Enabled = false;
        if (failure == "unverified_deployment") host.Assurance.IsAvailable = false;
        if (failure == "expired_proof") host.Clock.Advance(TimeSpan.FromMinutes(6));
        using var response = await browser.Bootstrap(proof, await browser.Csrf(), failure == "wrong_secret" ? "wrong" : FixtureSecret.Value);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        await using var db = PostgresFixture.Identity(connection);
        Assert.Empty(await db.Set<Account>().ToListAsync());
        Assert.Empty(await db.Set<ExternalIdentity>().ToListAsync());
        Assert.Empty(await db.Set<PlatformGrant>().ToListAsync());
        Assert.Empty(await db.Set<AccountSession>().ToListAsync());
        Assert.Empty(await db.Set<AuthenticationAudit>().ToListAsync());
        Assert.Equal(SetupStatus.Incomplete, (await db.Set<SetupState>().SingleAsync()).Status);
    }

    [Fact]
    public async Task Competing_setup_has_exactly_one_owner_and_failure_after_partial_save_rolls_back()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var first = new Browser(host); using var second = new Browser(host);
        var a = await first.Proof("owner-a"); var b = await second.Proof("owner-b");
        host.Rollback.Enabled = true;
        using var failed = await first.Bootstrap(a, await first.Csrf());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        await using (var verify = PostgresFixture.Identity(connection))
        {
            Assert.Empty(await verify.Set<Account>().ToListAsync());
            Assert.Empty(await verify.Set<AccountSession>().ToListAsync());
            Assert.Empty(await verify.Set<AuthenticationAudit>().ToListAsync());
            Assert.Equal(SetupStatus.Incomplete, (await verify.Set<SetupState>().SingleAsync()).Status);
            Assert.Null((await verify.Set<PendingLogin>().SingleAsync(p => p.Id == a)).ConsumedAt);
            Assert.Single(await verify.Set<SetupAttempt>().Where(x => x.IsFailure).ToListAsync());
        }
        host.Rollback.Enabled = false;
        var tokenA = await first.Csrf(); var tokenB = await second.Csrf();
        var responses = await Task.WhenAll(first.Bootstrap(a, tokenA), second.Bootstrap(b, tokenB));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var response in responses) response.Dispose();
        await using var db = PostgresFixture.Identity(connection);
        Assert.Single(await db.Set<Account>().ToListAsync());
        Assert.Single(await db.Set<AccountSession>().ToListAsync());
        Assert.Single(await db.Set<AuthenticationAudit>().ToListAsync());
        Assert.Equal(1, await db.Set<PendingLogin>().CountAsync(p => p.ConsumedAt != null));
    }

    [Fact]
    public async Task Shared_rolling_setup_budgets_survive_failures_and_apply_per_flow_source_and_deployment()
    {
        var connection = await postgres.CreateDatabase();
        await using var setup = PostgresFixture.Identity(connection); await setup.Database.MigrateAsync();
        var clock = new TestClock();
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        async Task Reserve(Guid flow, string source)
        {
            await using var db = PostgresFixture.Identity(connection);
            await new PostgresSetupAttemptBudget(db, clock).Reserve(flow, Hash(source), default);
        }
        var sameFlow = Guid.CreateVersion7();
        for (var i = 0; i < 5; i++) await Reserve(sameFlow, "source-" + i);
        var flowLimit = await Assert.ThrowsAsync<AuthenticationFailure>(() => Reserve(sameFlow, "another-source"));
        Assert.Equal(429, flowLimit.Status);
        clock.Advance(TimeSpan.FromMinutes(11));
        for (var i = 0; i < 5; i++) await Reserve(Guid.CreateVersion7(), "same-source");
        await Assert.ThrowsAsync<AuthenticationFailure>(() => Reserve(Guid.CreateVersion7(), "same-source"));
        clock.Advance(TimeSpan.FromMinutes(11));
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Reserve(Guid.CreateVersion7(), "global-" + i)));
        await Assert.ThrowsAsync<AuthenticationFailure>(() => Reserve(Guid.CreateVersion7(), "31st"));
        clock.Advance(TimeSpan.FromMinutes(2));
        await Reserve(Guid.CreateVersion7(), "later");
    }

    [Fact]
    public async Task Multiple_accounts_select_explicitly_and_logout_never_rebinds_other_tabs_or_browsers()
    {
        var (_, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host); using var another = new Browser(host);
        var owner = await Owner(browser);
        var member = await browser.Complete(await browser.Proof("member", "AddAccount"));
        var ownerElsewhere = await another.Complete(await another.Proof("owner", "Login"));
        Assert.Equal(owner.AccountId, ownerElsewhere.AccountId);
        using var noSelection = await browser.Context();
        var context = await noSelection.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, context.GetProperty("selected").ValueKind);
        Assert.Equal(2, context.GetProperty("sessions").GetArrayLength());
        var ownerToken = await browser.Csrf(owner.AccountSessionId);
        var memberToken = await browser.Csrf(member.AccountSessionId);
        using var mismatch = await browser.Post("/session/logout-current", new { }, ownerToken, member.AccountSessionId);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        using var foreign = await another.Context(member.AccountSessionId);
        Assert.Equal(HttpStatusCode.Unauthorized, foreign.StatusCode);
        using var logout = await browser.Post("/session/logout-current", new { }, ownerToken, owner.AccountSessionId);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var oldOwner = await browser.Context(owner.AccountSessionId);
        using var retainedMember = await browser.Context(member.AccountSessionId);
        using var retainedOtherBrowser = await another.Context(ownerElsewhere.AccountSessionId);
        Assert.Equal(HttpStatusCode.Unauthorized, oldOwner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retainedMember.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retainedOtherBrowser.StatusCode);
        using var all = await browser.Post("/session/logout-container", new { }, memberToken, member.AccountSessionId);
        Assert.Equal(HttpStatusCode.NoContent, all.StatusCode);
        using var revokedContainer = await browser.Authority(member);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedContainer.StatusCode);
        using var unaffected = await another.Context(ownerElsewhere.AccountSessionId);
        Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
    }

    [Fact]
    public async Task Parallel_tabs_can_both_send_selector_bound_account_writes_without_cookie_rotation()
    {
        var (_, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host);
        var a = await Owner(browser);
        var b = await browser.Complete(await browser.Proof("member", "Register"));
        var first = await browser.Csrf(a.AccountSessionId); var second = await browser.Csrf(b.AccountSessionId);
        var responses = await Task.WhenAll(browser.Post("/session/logout-current", new { }, first, a.AccountSessionId),
            browser.Post("/session/logout-current", new { }, second, b.AccountSessionId));
        foreach (var response in responses) { using (response) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); }
    }

    [Fact]
    public async Task Idle_and_absolute_expiration_deny_even_with_activity_and_valid_browser_credentials()
    {
        var (_, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host);
        var owner = await Owner(browser);
        host.Clock.Advance(TimeSpan.FromMinutes(31));
        using var idle = await browser.Context(owner.AccountSessionId);
        Assert.Equal(HttpStatusCode.Unauthorized, idle.StatusCode);
        var newSession = await browser.Complete(await browser.Proof("owner", "Login"));
        for (var i = 0; i < 23; i++)
        {
            host.Clock.Advance(TimeSpan.FromMinutes(29));
            using var active = await browser.Context(newSession.AccountSessionId);
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        }
        host.Clock.Advance(TimeSpan.FromMinutes(23)); // past the container/first-session absolute 12-hour boundary
        using var absolute = await browser.Context(newSession.AccountSessionId);
        Assert.Equal(HttpStatusCode.Unauthorized, absolute.StatusCode);
    }

    private static async Task StatusFixture(string connection, Guid accountId, AccountStatus status, DateTimeOffset now)
    {
        await using var db = PostgresFixture.Identity(connection);
        await using var tx = await db.Database.BeginTransactionAsync();
        var account = (await db.Set<Account>().FromSqlInterpolated($"SELECT * FROM accounts WHERE id={accountId} FOR UPDATE").ToListAsync()).Single();
        var previous = account.Status; var expected = account.Version;
        db.Entry(account).Property(a => a.Status).CurrentValue = status;
        account.InvalidateAuthentication(now);
        foreach (var session in await db.Set<AccountSession>().Where(s => s.AccountId == accountId).ToListAsync()) session.Revoke(now);
        db.Add(new AccountStatusTransition(Guid.CreateVersion7(), accountId, accountId, previous, status,
            status == AccountStatus.Suspended ? "Controlled fixture suspension" : null, now, expected, account.Version, Guid.CreateVersion7().ToString()));
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    [Fact]
    public async Task Suspension_revokes_all_target_sessions_and_reinstatement_requires_new_proof_without_affecting_other_accounts()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host); using var other = new Browser(host);
        var owner = await Owner(browser);
        var member = await browser.Complete(await browser.Proof("member", "Register"));
        var ownerOther = await other.Complete(await other.Proof("owner", "Login"));
        var staleProof = await other.Proof("owner", "Login");
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await StatusFixture(connection, owner.AccountId, AccountStatus.Suspended, host.Clock.GetUtcNow());
        using var suspended = await browser.Context(owner.AccountSessionId);
        using var suspendedElsewhere = await other.Context(ownerOther.AccountSessionId);
        using var independent = await browser.Context(member.AccountSessionId);
        Assert.Equal(HttpStatusCode.Forbidden, suspended.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, suspendedElsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await StatusFixture(connection, owner.AccountId, AccountStatus.Active, host.Clock.GetUtcNow());
        using var oldSession = await other.Context(ownerOther.AccountSessionId);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        using var oldProof = await other.Post("/auth/complete", new { pendingLoginId = staleProof }, await other.Csrf());
        Assert.Equal(HttpStatusCode.Unauthorized, oldProof.StatusCode);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var renewed = await other.Complete(await other.Proof("owner", "Login"));
        Assert.Equal(owner.AccountId, renewed.AccountId);
        using var fresh = await other.Context(renewed.AccountSessionId);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using var forbiddenWrite = await browser.Post("/platform/users/" + owner.AccountId + "/suspend", new { }, await browser.Csrf());
        Assert.Equal(HttpStatusCode.NotFound, forbiddenWrite.StatusCode);
    }

    [Fact]
    public async Task Internal_authority_requires_certificate_current_capability_fresh_assurance_and_request_binding()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host);
        var owner = await Owner(browser);
        using var absent = await browser.Authority(owner, certificate: "");
        using var wrong = await browser.Authority(owner, certificate: "other");
        Assert.Equal(HttpStatusCode.Forbidden, absent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        var requestId = Guid.CreateVersion7();
        using var allowed = await browser.Authority(owner, "platform.companies.review", requestId: requestId);
        Assert.True(allowed.StatusCode == HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
        var evidence = (await allowed.Content.ReadFromJsonAsync<AuthorityEvidence>())!;
        Assert.Equal(requestId, evidence.RequestId);
        Assert.Equal("organization", evidence.Service);
        Assert.Equal("platform.companies.review", evidence.Action);
        Assert.Equal(new[] { "platform.companies.review" }, evidence.Identity.Capabilities);
        Assert.Equal(owner.AccountId, evidence.Identity.AccountId);
        await using (var db = PostgresFixture.Identity(connection))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE platform_grants SET revoked_at={host.Clock.GetUtcNow()}, revoked_by_account_id={owner.AccountId} WHERE account_id={owner.AccountId} AND capability_key='platform.companies.review'");
        }
        using var revoked = await browser.Authority(owner, "platform.companies.review");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        using var stale = await browser.Authority(owner, "platform.companies.read");
        using var basic = await browser.Authority(owner);
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, basic.StatusCode);
        Assert.Empty((await basic.Content.ReadFromJsonAsync<AuthorityEvidence>())!.Identity.Capabilities);
        var reauthentication = await browser.BeginProof("Login", requireFreshMfa: true);
        Assert.Equal("300", QueryHelpers.ParseQuery(reauthentication.Query)["max_age"]);
        Assert.Contains("amr", QueryHelpers.ParseQuery(reauthentication.Query)["claims"].ToString());
    }

    [Fact]
    public async Task Registration_operation_is_server_issued_stable_and_never_an_email_merge()
    {
        var (_, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host);
        var owner = await Owner(browser); // simulator gives every subject the same mutable email
        var member = await browser.Complete(await browser.Proof("new-subject", "Register"));
        Assert.NotEqual(owner.AccountId, member.AccountId);
        Assert.NotNull(member.InitialRegistrationOperationId);
        var repeated = await browser.Complete(await browser.Proof("new-subject", "Login"));
        Assert.Equal(member.AccountId, repeated.AccountId);
        Assert.Equal(member.InitialRegistrationOperationId, repeated.InitialRegistrationOperationId);
        using var oldSession = await browser.Context(member.AccountSessionId);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
    }

    [Fact]
    public async Task Required_database_unavailable_fails_closed_for_session_and_authenticated_authority()
    {
        // Database destruction is isolated from the suite's accumulated migration databases
        // and checkpoints, as well as every developer resource.
        var isolated = new PostgresFixture(); await isolated.InitializeAsync();
        try
        {
            var connection = await isolated.CreateDatabase();
            await using (var db = PostgresFixture.Identity(connection)) await db.Database.MigrateAsync();
            await using var host = new GoogleTestHost(connection, enableCompletion: true);
            using var browser = new Browser(host);
            var owner = await Owner(browser);
            // This uniquely named database was created by this test fixture in its own disposable container.
            await isolated.DropOwnedDatabase(connection);
            using var session = await browser.Context(owner.AccountSessionId);
            using var authority = await browser.Authority(owner);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, session.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, authority.StatusCode);
        }
        finally { await isolated.DisposeAsync(); }
    }

    internal static async Task WaitForLock(string connection, string fragment)
    {
        await using var observer = new NpgsqlConnection(connection); await observer.OpenAsync();
        for (var i = 0; i < 150; i++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=@database AND wait_event_type='Lock' AND query LIKE @fragment)", observer);
            command.Parameters.AddWithValue("database", new NpgsqlConnectionStringBuilder(connection).Database!);
            command.Parameters.AddWithValue("fragment", "%" + fragment + "%");
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(20);
        }
        Assert.Fail("The request must demonstrably wait on the held PostgreSQL row lock.");
    }

    [Fact]
    public async Task Session_expiring_while_waiting_for_browser_lock_is_not_extended()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host);
        var owner = await Owner(browser);
        DateTimeOffset storedIdle;
        await using (var before = PostgresFixture.Identity(connection))
            storedIdle = await before.Set<AccountSession>().Select(s => s.IdleExpiresAt).SingleAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(50));
        await using var blocker = PostgresFixture.Identity(connection);
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("SELECT id FROM browser_sessions FOR UPDATE");
        var request = browser.Context(owner.AccountSessionId);
        try
        {
            await WaitForLock(connection, "browser_sessions");
            host.Clock.Advance(TimeSpan.FromSeconds(20));
        }
        finally { await tx.CommitAsync(); }
        using var denied = await request;
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await using var verify = PostgresFixture.Identity(connection);
        Assert.Equal(storedIdle, (await verify.Set<AccountSession>().SingleAsync()).IdleExpiresAt);
    }

    [Fact]
    public async Task Setup_proof_and_assurance_expiring_while_waiting_for_setup_lock_fail_closed()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host);
        var proof = await browser.Proof("owner", token: new(Subject: "owner", AuthenticatedAt: host.Clock.GetUtcNow()));
        var csrf = await browser.Csrf();
        host.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(50));
        await using var blocker = PostgresFixture.Identity(connection);
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("SELECT id FROM setup_state FOR UPDATE");
        var request = browser.Bootstrap(proof, csrf);
        try
        {
            await WaitForLock(connection, "setup_state");
            host.Clock.Advance(TimeSpan.FromSeconds(20));
        }
        finally { await tx.CommitAsync(); }
        using var denied = await request;
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        await using var verify = PostgresFixture.Identity(connection);
        Assert.Empty(await verify.Set<Account>().ToListAsync());
        Assert.Null((await verify.Set<PendingLogin>().SingleAsync()).ConsumedAt);
    }

    [Fact]
    public async Task Flow_started_before_suspension_cannot_finish_delayed_callback_after_reinstatement()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host);
        var owner = await Owner(browser);
        var challenge = await browser.BeginProof("Login");
        var code = host.Provider.IssueCode(challenge, new(Subject: "owner"));
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await StatusFixture(connection, owner.AccountId, AccountStatus.Suspended, host.Clock.GetUtcNow());
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await StatusFixture(connection, owner.AccountId, AccountStatus.Active, host.Clock.GetUtcNow());
        var delayed = await browser.FinishProof(challenge, code);
        using var denied = await browser.Post("/auth/complete", new { pendingLoginId = delayed }, await browser.Csrf());
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var fresh = await browser.Complete(await browser.Proof("owner", "Login"));
        Assert.Equal(owner.AccountId, fresh.AccountId);
    }

    [Fact]
    public async Task Callback_waiting_past_pending_flow_expiry_cannot_refresh_the_proof_deadline()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = new Browser(host);
        var challenge = await browser.BeginProof("Bootstrap");
        var code = host.Provider.IssueCode(challenge);
        var state = QueryHelpers.ParseQuery(challenge.Query)["state"].ToString();
        host.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(50));
        await using var blocker = PostgresFixture.Identity(connection);
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("SELECT id FROM pending_logins FOR UPDATE");
        var request = browser.Client.GetAsync(QueryHelpers.AddQueryString("/signin-google",
            new Dictionary<string, string?> { ["code"] = code, ["state"] = state }));
        try
        {
            await WaitForLock(connection, "pending_logins");
            host.Clock.Advance(TimeSpan.FromSeconds(20));
        }
        finally { await tx.CommitAsync(); }
        using var denied = await request;
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        await using var verify = PostgresFixture.Identity(connection);
        Assert.Null((await verify.Set<PendingLogin>().SingleAsync()).ValidatedAt);
        Assert.Empty(await verify.Set<Account>().ToListAsync());
    }
}
