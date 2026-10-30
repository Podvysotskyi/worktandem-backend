using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Worktandem.Identity.Application;
using Worktandem.Identity.Domain;
using Worktandem.Identity.Infrastructure;
using Xunit;

namespace Worktandem.PersistenceTests;

[Collection("postgres")]
public sealed class GoogleLoginTests(PostgresFixture postgres)
{
    private static async Task<string> Token(HttpClient browser, Guid? selector = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/context");
        if (selector is { } id) request.Headers.Add("X-WT-Account-Session", id.ToString("D"));
        using var response = await browser.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("csrfToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Start(HttpClient browser, string token, string purpose = "Bootstrap",
        string returnPath = "/auth/continue", Guid? selector = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/start")
        {
            Content = JsonContent.Create(new { purpose, returnPath })
        };
        request.Headers.Add("X-WT-CSRF", token);
        request.Headers.Add("Origin", "https://localhost");
        if (selector is { } id) request.Headers.Add("X-WT-Account-Session", id.ToString("D"));
        return await browser.SendAsync(request);
    }

    private static string Callback(Uri challenge, string code, string? state = null)
    {
        var original = QueryHelpers.ParseQuery(challenge.Query)["state"].ToString();
        return QueryHelpers.AddQueryString("/signin-google", new Dictionary<string, string?>
        { ["code"] = code, ["state"] = state ?? original });
    }

    private async Task<(string Connection, GoogleTestHost Host)> Host()
    {
        var connection = await postgres.CreateDatabase();
        await using var db = PostgresFixture.Identity(connection);
        await db.Database.MigrateAsync();
        return (connection, new GoogleTestHost(connection));
    }

    [Theory]
    [InlineData(false, false, 400)]
    [InlineData(false, true, 302)]
    [InlineData(true, false, 503)]
    [InlineData(true, true, 302)]
    public async Task Validated_callback_workflow_failure_uses_browser_recovery_and_preserves_api_status(bool databaseUnavailable, bool html, int expected)
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = host.Browser();
        using var challenge = await Start(browser, await Token(browser));
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var url = challenge.Headers.Location!;
        var code = host.Provider.IssueCode(url);
        if (databaseUnavailable) await postgres.DropOwnedDatabase(connection);
        else host.Clock.Advance(TimeSpan.FromMinutes(6));
        using var request = new HttpRequestMessage(HttpMethod.Get, Callback(url, code));
        request.Headers.Add("Accept", html ? "text/html" : "application/json");
        using var response = await browser.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(html ? "/auth/continue?error=invalid_google_login" : null, response.Headers.Location?.OriginalString);
        if (!html)
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(databaseUnavailable ? "identity_unavailable" : "invalid_google_login", payload.GetProperty("code").GetString());
        }
        Assert.DoesNotContain(code, await response.Content.ReadAsStringAsync());
        Assert.NotEmpty(host.Provider.LastVerifier!); // The framework validated the provider first.
    }

    [Theory]
    [InlineData("text/html", 302)]
    [InlineData("application/json", 400)]
    [InlineData("text/html;q=0", 400)]
    [InlineData("invalid-accept", 400)]
    public async Task Callback_failure_uses_a_fixed_browser_recovery_route_without_echoing_provider_input(string accept, int expected)
    {
        var (_, host) = await Host(); await using var dispose = host;
        using var browser = host.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/signin-google?code=secret-code-fixture&state=secret-state-fixture&error=provider-fixture&error_description=sensitive-description-fixture");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        using var response = await browser.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(expected == 302 ? "/auth/continue?error=invalid_google_login" : null, response.Headers.Location?.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("fixture", body);
        Assert.DoesNotContain("fixture", response.Headers.Location?.OriginalString ?? "");
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("text/html, application/json;q=0.8", true)]
    [InlineData("application/json;q=0", false)]
    [InlineData("invalid-accept", false)]
    public async Task Challenge_negotiation_preserves_framework_state_correlation_pkce_and_callback_validation(string accept, bool json)
    {
        var (_, host) = await Host(); await using var dispose = host;
        using var browser = host.Browser();
        var token = await Token(browser);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/start")
        { Content = JsonContent.Create(new { purpose = "Bootstrap", returnPath = "/setup" }) };
        request.Headers.Add("X-WT-CSRF", token);
        request.Headers.Add("Origin", "https://localhost");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        using var challenge = await browser.SendAsync(request);
        Assert.Equal(json ? HttpStatusCode.OK : HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Equal("no-store", challenge.Headers.CacheControl!.ToString());
        Assert.True(challenge.Headers.GetValues("Set-Cookie").Count() >= 2);
        var url = challenge.Headers.Location;
        if (json)
        {
            Assert.Null(url);
            var payload = await challenge.Content.ReadFromJsonAsync<JsonElement>();
            url = new Uri(payload.GetProperty("authorizationUrl").GetString()!);
        }
        Assert.NotNull(url);
        Assert.Equal("https://accounts.google.com", url.GetLeftPart(UriPartial.Authority));
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.NotEmpty(query["state"].ToString());
        Assert.NotEmpty(query["nonce"].ToString());
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("https://localhost/signin-google", query["redirect_uri"]);
        using var callback = await browser.GetAsync(Callback(url, host.Provider.IssueCode(url)));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.StartsWith("/setup?pendingLogin=", callback.Headers.Location!.OriginalString);
        Assert.NotEmpty(host.Provider.LastVerifier!);
    }

    [Fact]
    public async Task Framework_code_flow_uses_pkce_and_stores_only_browser_bound_temporary_proof()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = host.Browser();
        var csrf = await Token(browser);
        using var challenge = await Start(browser, csrf);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var url = challenge.Headers.Location!;
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.NotEmpty(query["state"].ToString());
        Assert.NotEmpty(query["nonce"].ToString());
        Assert.Equal("300", query["max_age"]);
        Assert.Contains("amr", query["claims"].ToString());
        Assert.DoesNotContain("offline_access", query["scope"].ToString());
        var code = host.Provider.IssueCode(url);
        using var callback = await browser.GetAsync(Callback(url, code));
        Assert.True(callback.StatusCode == HttpStatusCode.Redirect,
            "Expected a validated callback; simulator: " + host.Provider.ValidationFailure + "; framework: " + host.OidcFailure +
            "; categories: " + string.Join("; ", host.Logs.Messages.Where(message => message.Contains("Google proof rejected", StringComparison.Ordinal))));
        Assert.NotEmpty(host.Provider.LastVerifier!);
        await using var db = PostgresFixture.Identity(connection);
        var pending = await db.Set<PendingLogin>().SingleAsync();
        Assert.Equal("fixture-subject", pending.Subject);
        Assert.Equal("https://accounts.google.com", pending.Issuer);
        Assert.True(pending.MfaObserved);
        Assert.True(pending.EmailVerified);
        Assert.NotNull(pending.ValidatedAt);
        Assert.Equal(TimeSpan.FromMinutes(5), pending.ExpiresAt - pending.ValidatedAt);
        Assert.Empty(await db.Set<Account>().ToListAsync());
        Assert.Empty(await db.Set<ExternalIdentity>().ToListAsync());
        Assert.Empty(await db.Set<AccountSession>().ToListAsync());
        Assert.Empty(await db.Set<PlatformGrant>().ToListAsync());
        Assert.Equal(SetupStatus.Incomplete, (await db.Set<SetupState>().SingleAsync()).Status);
        using var ownRead = await browser.GetAsync("/auth/pending/" + pending.Id);
        Assert.Equal(HttpStatusCode.OK, ownRead.StatusCode);
        using var stranger = host.Browser();
        using var foreignRead = await stranger.GetAsync("/auth/pending/" + pending.Id);
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        using var prohibited = await browser.PostAsJsonAsync("/setup/complete", new { pendingLoginId = pending.Id, secret = "fixture-never-processed" });
        Assert.Equal(HttpStatusCode.BadRequest, prohibited.StatusCode);
        // A copied/replayed callback cannot create another proof or account.
        using var replay = await browser.GetAsync(Callback(url, code));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Single(await db.Set<PendingLogin>().ToListAsync());
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains(code, StringComparison.Ordinal) ||
            message.Contains(query["state"].ToString(), StringComparison.Ordinal) ||
            message.Contains(host.Provider.LastIdToken!, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("nonce")]
    [InlineData("missing_nonce")]
    [InlineData("signature")]
    [InlineData("state")]
    [InlineData("missing_correlation")]
    public async Task Invalid_provider_or_flow_proof_never_validates_pending_login(string failure)
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = host.Browser();
        using var challenge = await Start(browser, await Token(browser));
        var url = challenge.Headers.Location!;
        var token = failure switch
        {
            "issuer" => new GoogleSimulator.TokenCase(Issuer: "https://attacker.invalid"),
            "audience" => new GoogleSimulator.TokenCase(Audience: "another-client"),
            "expired" => new GoogleSimulator.TokenCase(Expired: true),
            "nonce" => new GoogleSimulator.TokenCase(WrongNonce: true),
            "missing_nonce" => new GoogleSimulator.TokenCase(MissingNonce: true),
            "signature" => new GoogleSimulator.TokenCase(WrongSignature: true),
            _ => new GoogleSimulator.TokenCase()
        };
        var code = host.Provider.IssueCode(url, token);
        using var stranger = host.Browser();
        using var result = await (failure == "missing_correlation" ? stranger : browser)
            .GetAsync(Callback(url, code, failure == "state" ? "tampered-state" : null));
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        var problem = await result.Content.ReadAsStringAsync();
        Assert.Contains("invalid_google_login", problem);
        Assert.DoesNotContain(code, problem);
        await using var db = PostgresFixture.Identity(connection);
        Assert.Null((await db.Set<PendingLogin>().SingleAsync()).ValidatedAt);
        Assert.Empty(await db.Set<Account>().ToListAsync());
    }

    [Fact]
    public async Task Missing_or_stale_mfa_evidence_cannot_enable_bootstrap_or_any_account_authority()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = host.Browser();
        using var challenge = await Start(browser, await Token(browser));
        var url = challenge.Headers.Location!;
        var code = host.Provider.IssueCode(url, new(Mfa: false, StaleAuthTime: true));
        using var response = await browser.GetAsync(Callback(url, code));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var db = PostgresFixture.Identity(connection);
        var pending = await db.Set<PendingLogin>().SingleAsync();
        Assert.False(pending.MfaObserved);
        Assert.True(pending.ProviderAuthenticatedAt < host.Clock.GetUtcNow().AddMinutes(-5));
        using var complete = await browser.PostAsJsonAsync("/setup/complete", new { pendingLoginId = pending.Id });
        Assert.Equal(HttpStatusCode.BadRequest, complete.StatusCode);
        Assert.Empty(await db.Set<Account>().ToListAsync());
        Assert.Empty(await db.Set<AccountSession>().ToListAsync());
        Assert.Empty(await db.Set<PlatformGrant>().ToListAsync());
    }

    [Fact]
    public async Task Completed_setup_closes_bootstrap_and_allows_only_temporary_normal_login_flow()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        await using var db = PostgresFixture.Identity(connection);
        var now = DateTimeOffset.UtcNow; var ownerId = Guid.CreateVersion7();
        db.Add(new Account(ownerId, "Fixture existing owner", AccountStatus.Active, null, now, now, Guid.CreateVersion7()));
        db.Add(new ExternalIdentity(Guid.CreateVersion7(), ownerId, "Google", "https://accounts.google.com", "existing-owner",
            "same-mutable-mailbox@example.invalid", true, null, now, now, Guid.CreateVersion7()));
        await db.SaveChangesAsync();
        var setup = await db.Set<SetupState>().SingleAsync();
        setup.Complete(ownerId, now); await db.SaveChangesAsync();
        using var browser = host.Browser();
        var csrf = await Token(browser);
        using var closed = await Start(browser, csrf);
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        using var normal = await Start(browser, csrf, "Register");
        Assert.Equal(HttpStatusCode.Redirect, normal.StatusCode);
        var url = normal.Headers.Location!;
        using var proof = await browser.GetAsync(Callback(url, host.Provider.IssueCode(url, new(Subject: "different-google-subject"))));
        Assert.Equal(HttpStatusCode.Redirect, proof.StatusCode);
        Assert.Single(await db.Set<Account>().ToListAsync());
        Assert.Single(await db.Set<ExternalIdentity>().ToListAsync());
        Assert.Empty(await db.Set<AccountSession>().ToListAsync());
    }

    [Fact]
    public async Task Normal_registration_login_and_add_account_are_closed_before_bootstrap()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = host.Browser();
        var csrf = await Token(browser);
        foreach (var purpose in new[] { "Register", "Login", "AddAccount" })
        {
            using var response = await Start(browser, csrf, purpose);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("setup_required", await response.Content.ReadAsStringAsync());
        }
        await using var db = PostgresFixture.Identity(connection);
        Assert.Empty(await db.Set<Account>().ToListAsync());
        Assert.Empty(await db.Set<PendingLogin>().ToListAsync());
    }

    [Fact]
    public async Task Antiforgery_tokens_are_container_and_selector_bound_and_tabs_remain_concurrently_valid()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = host.Browser();
        var first = Guid.CreateVersion7(); var second = Guid.CreateVersion7();
        var a = await Token(browser, first); var b = await Token(browser, second);
        using var crossed = await Start(browser, a, selector: second);
        Assert.Equal(HttpStatusCode.BadRequest, crossed.StatusCode);
        using var stranger = host.Browser();
        _ = await Token(stranger, first);
        using var stolen = await Start(stranger, a, selector: first);
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);
        var responses = await Task.WhenAll(Start(browser, a, selector: first), Start(browser, b, selector: second));
        foreach (var response in responses) { using (response) Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); }
        await using var db = PostgresFixture.Identity(connection);
        Assert.Equal(2, await db.Set<PendingLogin>().CountAsync());
        Assert.Single(await db.Set<BrowserSession>().ToListAsync());
        Assert.Empty(await db.Set<AccountSession>().ToListAsync());
    }

    [Fact]
    public async Task Unsafe_start_rejects_missing_csrf_foreign_origin_and_unsafe_return_destinations()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = host.Browser();
        var csrf = await Token(browser);
        using var missing = await browser.PostAsJsonAsync("/auth/start", new { purpose = "Bootstrap", returnPath = "/" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/auth/start")
        { Content = JsonContent.Create(new { purpose = "Bootstrap", returnPath = "/" }) };
        foreign.Headers.Add("X-WT-CSRF", csrf); foreign.Headers.Add("Origin", "https://attacker.invalid");
        using var result = await browser.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        foreach (var path in new[] { "https://attacker.invalid", "//attacker.invalid", "/%2f%2fattacker.invalid", "/\\attacker.invalid" })
        {
            using var invalid = await Start(browser, csrf, returnPath: path);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var get = await browser.GetAsync("/auth/start");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        await using var db = PostgresFixture.Identity(connection);
        Assert.Empty(await db.Set<PendingLogin>().ToListAsync());
    }

    [Fact]
    public async Task Expired_flow_fails_and_each_browser_has_a_shared_durable_active_flow_limit()
    {
        var (connection, host) = await Host(); await using var dispose = host;
        using var browser = host.Browser();
        var csrf = await Token(browser);
        Uri? first = null;
        for (var i = 0; i < 5; i++)
        {
            using var response = await Start(browser, csrf);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            first ??= response.Headers.Location;
        }
        using var limited = await Start(browser, csrf);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        var code = host.Provider.IssueCode(first!);
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        using var expired = await browser.GetAsync(Callback(first!, code));
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        await using var db = PostgresFixture.Identity(connection);
        Assert.Equal(5, await db.Set<PendingLogin>().CountAsync());
        Assert.Empty(await db.Set<PendingLogin>().Where(p => p.ValidatedAt != null).ToListAsync());
    }

    [Fact]
    public async Task Proof_store_rejects_concurrent_validation_replay_and_email_collision_does_not_create_accounts()
    {
        var connection = await postgres.CreateDatabase();
        await using (var db = PostgresFixture.Identity(connection)) await db.Database.MigrateAsync();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-browser-fixture"))).ToLowerInvariant();
        Guid id;
        await using (var db = PostgresFixture.Identity(connection))
            id = await new PostgresPendingLoginStore(db, TimeProvider.System).Begin(digest, LoginPurpose.Bootstrap, "/", default);
        async Task<bool> Validate(string subject)
        {
            await using var db = PostgresFixture.Identity(connection);
            try
            {
                await new PostgresPendingLoginStore(db, TimeProvider.System).Validate(id, digest,
                    new("https://accounts.google.com", subject, "same@example.invalid", true, null, DateTimeOffset.UtcNow, true), default);
                return true;
            }
            catch (LoginFlowException) { return false; }
        }
        var result = await Task.WhenAll(Validate("subject-one"), Validate("subject-two"));
        Assert.Equal(1, result.Count(success => success));
        await using var verify = PostgresFixture.Identity(connection);
        Assert.Empty(await verify.Set<Account>().ToListAsync());
        Assert.Empty(await verify.Set<ExternalIdentity>().ToListAsync());
        Assert.NotNull((await verify.Set<PendingLogin>().SingleAsync()).ValidatedAt);
    }

    [Fact]
    public async Task Missing_runtime_configuration_keeps_authentication_unavailable_and_no_bootstrap_handler_exists()
    {
        await using var host = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Identity.Api.Program>();
        using var browser = host.CreateClient();
        using var unavailable = await browser.GetAsync("/auth/context");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Empty(unavailable.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : []);
        using var setup = await browser.PostAsJsonAsync("/setup/complete", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, setup.StatusCode);
    }
}
