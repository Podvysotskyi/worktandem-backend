using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Worktandem.PersistenceTests;

// Test-host backchannel only. No production adapter can select this provider or its keys.
internal sealed class GoogleSimulator : HttpMessageHandler
{
    private readonly RSA key = RSA.Create(2048);
    private readonly RSA wrongKey = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Code> codes = new();
    private int sequence;
    public string? LastVerifier { get; private set; }
    public string? LastIdToken { get; private set; }
    public string? ValidationFailure { get; private set; }
    public OpenIdConnectConfiguration Configuration { get; }

    public GoogleSimulator()
    {
        Configuration = new OpenIdConnectConfiguration
        {
            Issuer = "https://accounts.google.com",
            AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
            TokenEndpoint = "https://oauth2.googleapis.com/token"
        };
        Configuration.SigningKeys.Add(new RsaSecurityKey(key) { KeyId = "fixture-google-key" });
    }

    public sealed record TokenCase(string Issuer = "https://accounts.google.com", string Audience = "fixture-client",
        string Subject = "fixture-subject", bool Expired = false, bool WrongNonce = false,
        bool MissingNonce = false, bool WrongSignature = false, bool Mfa = true, bool StaleAuthTime = false,
        DateTimeOffset? AuthenticatedAt = null, bool MissingAuthTime = false);

    private sealed record Code(string Challenge, string Nonce, TokenCase Token);

    public string IssueCode(Uri challenge, TokenCase? token = null)
    {
        var query = QueryHelpers.ParseQuery(challenge.Query);
        var code = "fixture-code-" + Interlocked.Increment(ref sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        codes[code] = new Code(query["code_challenge"].ToString(), query["nonce"].ToString(), token ?? new());
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsoluteUri != Configuration.TokenEndpoint || request.Method != HttpMethod.Post)
            throw new InvalidOperationException("Unexpected test backchannel request.");
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (!codes.TryRemove(form["code"].ToString(), out var code))
            return Error();
        LastVerifier = form["code_verifier"].ToString();
        var actualChallenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(LastVerifier)));
        if (actualChallenge != code.Challenge) { ValidationFailure = "PKCE mismatch"; return Error(); }
        if (form["client_id"] != "fixture-client") { ValidationFailure = "client ID mismatch"; return Error(); }
        if (form["client_secret"] != "fixture-client-secret") { ValidationFailure = "client credential mismatch"; return Error(); }
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = code.Token.Subject,
            ["email"] = "same-mutable-mailbox@example.invalid",
            ["email_verified"] = true,
            ["auth_time"] = (code.Token.AuthenticatedAt ?? DateTimeOffset.UtcNow.AddMinutes(code.Token.StaleAuthTime ? -10 : -1)).ToUnixTimeSeconds()
        };
        if (code.Token.MissingAuthTime) claims.Remove("auth_time");
        if (!code.Token.MissingNonce) claims["nonce"] = code.Token.WrongNonce ? "incorrect-nonce" : code.Nonce;
        if (code.Token.Mfa) claims["amr"] = new[] { "mfa" };
        LastIdToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = code.Token.Issuer,
            Audience = code.Token.Audience,
            Claims = claims,
            IssuedAt = now.AddMinutes(-10),
            NotBefore = now.AddMinutes(-10),
            Expires = now.AddMinutes(code.Token.Expired ? -2 : 5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(code.Token.WrongSignature ? wrongKey : key)
            { KeyId = "fixture-google-key" }, SecurityAlgorithms.RsaSha256)
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id_token = LastIdToken,
                access_token = "fixture-unused-access-token",
                token_type = "Bearer",
                expires_in = 300
            }), Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage Error() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { key.Dispose(); wrongKey.Dispose(); }
        base.Dispose(disposing);
    }
}
