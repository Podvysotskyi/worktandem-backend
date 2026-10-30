using System.Net;
using System.Net.Http.Json;
using Worktandem.Organization.Application;

namespace Worktandem.Organization.Api;

// Transport DTOs belong to this service's contract adapter, never to Identity persistence/projects.
public sealed class IdentityAuthorityClient(HttpClient http, TimeProvider clock) : IIdentityAuthority
{
    public async Task<OrganizationAuthority> Validate(ActorRequest actor, string action, CancellationToken ct)
    {
        var requestId = Guid.CreateVersion7();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/identity/validate-session")
            {
                Content = JsonContent.Create(new
                {
                    actor.BrowserCredential,
                    actor.AccountSessionId,
                    action,
                    requestId
                })
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new CompanyFailure("invalid_session", 401);
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new CompanyFailure("authority_denied", 403);
            if (!response.IsSuccessStatusCode) throw new CompanyFailure("identity_unavailable", 503);
            if (response.Content.Headers.ContentLength > 16384) throw new CompanyFailure("identity_unavailable", 503);
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var bounded = new MemoryStream();
            var buffer = new byte[4096];
            for (int read; (read = await stream.ReadAsync(buffer, token)) > 0;)
            {
                if (bounded.Length + read > 16384) throw new CompanyFailure("identity_unavailable", 503);
                bounded.Write(buffer, 0, read);
            }
            var evidence = System.Text.Json.JsonSerializer.Deserialize<OrganizationAuthority>(bounded.ToArray(),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            var now = clock.GetUtcNow();
            if (evidence is null || evidence.Identity is null || evidence.EvidenceId == Guid.Empty || evidence.RequestId != requestId ||
                evidence.Service != "organization" || evidence.Action != action || evidence.Identity.AccountId == Guid.Empty ||
                evidence.Identity.AccountSessionId != actor.AccountSessionId || evidence.ValidatedAt > now.AddSeconds(2) ||
                evidence.ValidatedAt < now.AddSeconds(-10) || evidence.Identity.ExpiresAt <= now || evidence.Identity.IdleExpiresAt <= now ||
                (action != "organization.account" && (evidence.Identity.Capabilities is null ||
                    !evidence.Identity.Capabilities.Contains(action, StringComparer.Ordinal))))
                throw new CompanyFailure("identity_unavailable", 503);
            return evidence;
        }
        catch (HttpRequestException) { throw new CompanyFailure("identity_unavailable", 503); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CompanyFailure("identity_unavailable", 503); }
        catch (System.Text.Json.JsonException) { throw new CompanyFailure("identity_unavailable", 503); }
    }
}
