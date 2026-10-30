using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Worktandem.SmokeTests;

public sealed class GatewayTests
{
    private sealed class Upstream : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Respond(request, cancellationToken); }
    }

    private static WebApplicationFactory<Gateway.Program> Host(Upstream upstream, string publicOrigin = "https://worktandem.test", string identityOrigin = "https://identity.test") =>
        new WebApplicationFactory<Gateway.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktandem:Gateway:PublicOrigin"] = publicOrigin,
                ["Worktandem:Gateway:IdentityOrigin"] = identityOrigin,
                ["Worktandem:Gateway:OrganizationOrigin"] = "https://organization.test"
            }));
            builder.ConfigureTestServices(services => services.AddHttpClient(Gateway.BrowserProxy.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => upstream));
        });
    private static HttpClient Browser(WebApplicationFactory<Gateway.Program> host, string origin = "https://worktandem.test") =>
        host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(origin), AllowAutoRedirect = false });

    [Theory]
    [InlineData("GET", "/api/identity/auth/context", "identity.test", "/auth/context")]
    [InlineData("GET", "/api/identity/session/context", "identity.test", "/session/context")]
    [InlineData("POST", "/api/identity/setup/complete", "identity.test", "/setup/complete")]
    [InlineData("POST", "/api/identity/session/logout-current", "identity.test", "/session/logout-current")]
    [InlineData("GET", "/api/organization/companies/context", "organization.test", "/companies/context")]
    [InlineData("POST", "/api/organization/companies/applications", "organization.test", "/companies/applications")]
    [InlineData("GET", "/signin-google?code=sensitive-fixture&state=state-fixture", "identity.test", "/signin-google")]
    public async Task Routes_only_to_the_configured_service_with_canonical_origin(string method, string path, string host, string target)
    {
        var upstream = new Upstream();
        upstream.Respond = async (request, _) =>
        {
            Assert.Equal(host, request.RequestUri!.Host);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal(target, request.RequestUri.AbsolutePath);
            Assert.Equal("worktandem.test", request.Headers.Host);
            Assert.Equal(method, request.Method.Method);
            if (method == "POST") Assert.Equal("{\"fixture\":true}", await request.Content!.ReadAsStringAsync());
            if (path.Contains('?')) Assert.Equal("?code=sensitive-fixture&state=state-fixture", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        };
        await using var factory = Host(upstream); using var browser = Browser(factory);
        using var message = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") message.Content = new StringContent("{\"fixture\":true}", System.Text.Encoding.UTF8, "application/json");
        using var response = await browser.SendAsync(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(1, upstream.Calls);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Theory]
    [InlineData("POST", "/api/identity/internal/identity/validate-session")]
    [InlineData("GET", "/api/identity/health/ready")]
    [InlineData("GET", "/api/identity/openapi/v1.json")]
    [InlineData("POST", "/api/organization/platform/users/123/suspend")]
    [InlineData("GET", "/api/organization/auth/context")]
    [InlineData("DELETE", "/api/organization/companies/applications")]
    [InlineData("GET", "/api/identity/auth/context/extra")]
    [InlineData("POST", "/api/identity/auth/context")]
    [InlineData("GET", "/api/identity/setup/complete")]
    [InlineData("POST", "/api/organization/companies/applications/../internal")]
    [InlineData("GET", "/api/identity//auth/context")]
    [InlineData("GET", "/api/identity/auth%2fcontext")]
    public async Task Unknown_internal_deferred_and_wrong_method_routes_never_reach_services(string method, string path)
    {
        var upstream = new Upstream(); await using var factory = Host(upstream); using var browser = Browser(factory);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.Equal(0, upstream.Calls);
    }

    [Theory]
    [InlineData("http://worktandem.test", "https://worktandem.test", "https://identity.test", 400)]
    [InlineData("https://attacker.test", "https://worktandem.test", "https://identity.test", 400)]
    [InlineData("https://worktandem.test", "", "https://identity.test", 503)]
    [InlineData("https://worktandem.test", "http://worktandem.test", "https://identity.test", 503)]
    [InlineData("https://worktandem.test", "https://worktandem.test", "http://identity.test", 503)]
    [InlineData("https://worktandem.test", "https://worktandem.test", "https://identity.test/path", 503)]
    [InlineData("https://worktandem.test", "https://worktandem.test", "https://user:pass@identity.test", 503)]
    public async Task Origin_configuration_and_tls_fail_closed(string origin, string configured, string identity, int expected)
    {
        var upstream = new Upstream(); await using var factory = Host(upstream, configured, identity); using var browser = Browser(factory, origin);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity/auth/context");
        request.Headers.Add("X-Forwarded-Proto", "https"); request.Headers.Add("X-Forwarded-Host", "worktandem.test");
        using var response = await browser.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode); Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task Only_protocol_headers_are_forwarded_and_multiple_secure_cookies_are_preserved()
    {
        var upstream = new Upstream();
        upstream.Respond = (request, _) =>
        {
            foreach (var name in new[] { "Forwarded", "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Client-Cert", "Authorization", "X-WT-Actor", "X-WT-Permissions", "Referer" })
                Assert.False(request.Headers.Contains(name));
            foreach (var name in new[] { "Cookie", "Origin", "X-WT-CSRF", "X-WT-Account-Session", "X-WT-Company", "If-Match", "Idempotency-Key" })
                Assert.True(request.Headers.Contains(name));
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { company = "fixture" }) };
            response.Headers.TryAddWithoutValidation("Set-Cookie", new[] { "__Host-wt-browser=opaque; path=/; secure; httponly", "__Host-wt-csrf=csrf; path=/; secure; httponly" });
            response.Headers.TryAddWithoutValidation("ETag", "\"version\"");
            response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "*");
            response.Headers.TryAddWithoutValidation("X-Internal-Detail", "private");
            return Task.FromResult(response);
        };
        await using var factory = Host(upstream); using var browser = Browser(factory);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/organization/companies/applications") { Content = JsonContent.Create(new { }) };
        foreach (var name in new[] { "Forwarded", "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Client-Cert", "Authorization", "X-WT-Actor", "X-WT-Permissions", "Referer", "Cookie", "Origin", "X-WT-CSRF", "X-WT-Account-Session", "X-WT-Company", "If-Match", "Idempotency-Key" })
            message.Headers.TryAddWithoutValidation(name, name == "Cookie" ? "__Host-wt-browser=opaque" : "fixture");
        using var response = await browser.SendAsync(message);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, response.Headers.GetValues("Set-Cookie").Count());
        Assert.Equal("\"version\"", response.Headers.ETag!.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin")); Assert.False(response.Headers.Contains("X-Internal-Detail"));
    }

    [Theory]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth?state=fixture", 302)]
    [InlineData("/auth/continue?pendingLogin=fixture", 302)]
    [InlineData("//attacker.test/", 502)]
    [InlineData("https://attacker.test/", 502)]
    [InlineData("http://accounts.google.com/", 502)]
    [InlineData("/internal/identity/validate-session", 502)]
    public async Task Redirects_are_relayed_without_following_and_unsafe_destinations_fail_closed(string location, int expected)
    {
        var upstream = new Upstream { Respond = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute); return Task.FromResult(response);
        }};
        await using var factory = Host(upstream); using var browser = Browser(factory);
        using var response = await browser.GetAsync("/signin-google");
        Assert.Equal(expected, (int)response.StatusCode); Assert.Equal(1, upstream.Calls);
        Assert.Equal(expected == 302 ? location : null, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Large_request_never_reaches_service_and_transport_failure_returns_safe_problem()
    {
        var upstream = new Upstream(); await using var factory = Host(upstream); using var browser = Browser(factory);
        using var oversized = await browser.PostAsync("/api/identity/setup/complete", new StringContent(new string('a', 32769)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode); Assert.Equal(0, upstream.Calls);
        upstream.Respond = (_, _) => throw new HttpRequestException("fixture sensitive connection details");
        using var failed = await browser.GetAsync("/api/identity/auth/context");
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.DoesNotContain("sensitive", await failed.Content.ReadAsStringAsync());
        Assert.Equal("service_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Client_abort_cancels_the_inflight_request()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new Upstream { Respond = async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }};
        await using var factory = Host(upstream); using var browser = Browser(factory);
        using var cancellation = new CancellationTokenSource();
        var request = browser.GetAsync("/api/identity/auth/context", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Real_transport_rejects_an_untrusted_service_certificate()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var certificateRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=worktandem.test", key, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        names.AddDnsName("worktandem.test");
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0,
            endpoint => Microsoft.AspNetCore.Hosting.ListenOptionsHttpsExtensions.UseHttps(endpoint, certificate)));
        await using var service = builder.Build();
        service.MapGet("/auth/context", () => "must not be read");
        await service.StartAsync();
        try
        {
            var addresses = service.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
            using var client = new HttpClient(Gateway.BrowserProxy.Handler());
            using var request = new HttpRequestMessage(HttpMethod.Get, addresses.Addresses.Single() + "/auth/context");
            request.Headers.Host = "worktandem.test";
            // Certificate exists only in memory; no machine trust is installed for the test.
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        }
        finally { await service.StopAsync(); }
    }
}
