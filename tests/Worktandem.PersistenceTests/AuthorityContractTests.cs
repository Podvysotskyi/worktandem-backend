using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Worktandem.Organization.Api;
using Worktandem.Organization.Application;
using Xunit;

namespace Worktandem.PersistenceTests;

public sealed class AuthorityContractTests
{
    private sealed class Reply(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request, ct);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("service")]
    [InlineData("action")]
    [InlineData("session")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("capability")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public async Task Authority_client_rejects_unbound_stale_or_invalid_response(string invalid)
    {
        var clock = new TestClock(); var actor = new ActorRequest(new string('a', 43), Guid.CreateVersion7());
        using var handler = new Reply(async (request, ct) =>
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var evidence = new OrganizationAuthority(Guid.CreateVersion7(), body.GetProperty("requestId").GetGuid(), "organization",
                "platform.companies.review", clock.GetUtcNow(), new AuthorityAccount(Guid.CreateVersion7(), actor.AccountSessionId, "Fixture",
                    Guid.CreateVersion7(), Guid.CreateVersion7(), clock.GetUtcNow().AddHours(1), clock.GetUtcNow().AddMinutes(30), ["platform.companies.review"]));
            evidence = invalid switch
            {
                "request" => evidence with { RequestId = Guid.CreateVersion7() },
                "service" => evidence with { Service = "another-service" },
                "action" => evidence with { Action = "organization.account" },
                "session" => evidence with { Identity = evidence.Identity with { AccountSessionId = Guid.CreateVersion7() } },
                "stale" => evidence with { ValidatedAt = clock.GetUtcNow().AddSeconds(-11) },
                "future" => evidence with { ValidatedAt = clock.GetUtcNow().AddSeconds(3) },
                "capability" => evidence with { Identity = evidence.Identity with { Capabilities = [] } },
                _ => evidence
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = invalid switch
                {
                    "malformed" => new StringContent("invalid json"),
                    "oversized" => new StringContent(new string('a', 20000)),
                    _ => JsonContent.Create(evidence)
                }
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://identity.invalid") };
        var failure = await Assert.ThrowsAsync<CompanyFailure>(() => new IdentityAuthorityClient(http, clock)
            .Validate(actor, "platform.companies.review", default));
        Assert.Equal(503, failure.Status);
    }
}
