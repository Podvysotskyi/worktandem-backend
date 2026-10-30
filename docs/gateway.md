# Browser gateway transport

The Gateway forwards a fixed allowlist of the implemented browser contracts. It does not
authenticate accounts, grant capabilities, choose an account/company, or access a database.
Identity and Organization still validate browser/session ownership, antiforgery, live authority
and company permissions on every applicable request. Their internal mutual-TLS authority
exchange is unchanged and is never exposed by this gateway.

| Public route | Downstream |
| --- | --- |
| `/api/identity/<documented Identity browser path>` | Identity, with `/api/identity` removed |
| `/api/organization/<documented Organization browser path>` | Organization, with `/api/organization` removed |
| `GET /signin-google` | Identity framework callback, unchanged |

Only the methods and exact route shapes documented in [authentication](authentication.md)
and [company onboarding](company-onboarding.md) are forwarded. Internal authority, downstream
health/OpenAPI, unsupported status writes, arbitrary paths, and methods fail with 404.
Route the above paths before frontend fallbacks at ingress. `/auth/continue` and `/setup`
are frontend pages, not gateway endpoints. Direct unprefixed API routes are not forwarded.

`POST /api/identity/auth/start` accepts `Accept: application/json` and returns
`{ "authorizationUrl": "https://accounts.google.com/..." }` after the framework has generated
the complete code-flow challenge, protected state, nonce and correlation cookies. The browser
must explicitly navigate to this URL after validating the exact Google HTTPS origin. Ordinary
clients retain the existing 302 challenge contract. This avoids fetch following Google's
cross-origin redirect and does not replace framework callback validation or PKCE.
Failed callbacks with `Accept: text/html` redirect to the fixed frontend recovery route
`/auth/continue?error=invalid_google_login`; other clients retain the safe 400 JSON problem.
No provider error description, code, state or arbitrary return URL is echoed into that redirect.

## Fixed HTTPS origin and deployment prerequisites

Supply server configuration using normal ASP.NET providers (reference-only keys also appear
in `.env.example`):

- `Worktandem__Gateway__PublicOrigin`: the one canonical external HTTPS origin, no path/query/userinfo.
- `Worktandem__Gateway__IdentityOrigin`: fixed HTTPS connection destination for Identity.
- `Worktandem__Gateway__OrganizationOrigin`: fixed HTTPS connection destination for Organization.

Missing/malformed configuration returns 503. Browser requests must arrive at Gateway over HTTPS
with the canonical Host, otherwise they return 400. Gateway deliberately does not enable
forwarded-header processing: an untrusted `Forwarded` or `X-Forwarded-*` value cannot turn HTTP
into HTTPS, select a destination, become a source identity or change the public callback origin.
Ingress therefore needs end-to-end TLS to Gateway, preserving the configured public Host.

Gateway sends the configured public Host over its separate HTTPS connections to the services.
This gives their antiforgery origin checks and OIDC callback construction the correct public
origin without caller-controlled forwarding headers. .NET uses the Host override for SNI and
certificate-name validation ([Microsoft documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-sni)).
Both downstream TLS listeners must present normally trusted certificates covering the canonical
public hostname, accept that Host, and be reachable at their configured connection destinations.
Normal certificate chain/name/revocation validation remains enabled. No certificate validation
callback, trust-store modification or client-certificate forwarding exists. A failed TLS
connection returns a safe 502. This repository does not provision those certificates or ingress.

Identity currently sees the Gateway's source IP, so its shared setup-attempt source-IP budget
aggregates browsers at that peer. This is stricter than a per-browser public-IP budget. A future
trusted ingress/source-IP integration requires explicit proxy trust configuration and its own
negative tests; arbitrary forwarded IP values are intentionally discarded here.

Browser cookies retain their original `__Host-` names, Secure/HttpOnly/SameSite flags and root
paths. Multiple Set-Cookie values are relayed independently. No cookie jar is shared between
browser requests. Request forwarding allows only browser protocol headers: cookies, Origin,
Fetch Metadata, selectors, CSRF, content negotiation, If-Match and Idempotency-Key. Caller
Authorization, actor/role/capability, certificate, forwarding and hop-by-hop headers are discarded.
The gateway has no privileged service credential that could bypass downstream session validation.

Responses are no-store, no-referrer and nosniff. Only protocol response headers are relayed;
credentialed CORS is not enabled. Redirects are never followed server-side and are restricted to
Google's exact HTTPS origin or the existing local completion paths. Request bodies are bounded
to 32 KiB before forwarding; upstream requests have a 30-second deadline and are cancelled when
the client disconnects. A timed-out write may already have committed: recover with the same
idempotency key/status query, never an automatic new mutation. Safe 502/504 problems contain no
exception/connection details. Gateway request-URL and outbound HttpClient logging are disabled
because callback queries contain one-use codes/state; ingress logging still needs redaction.

Gateway health remains process-only. Browser transport tests validate routing, header/cookie
handling, origin rejection, redirects, size limits, safe failures and cancellation using injected
handlers. The authentication JSON challenge test uses actual OIDC/callback validation and
disposable PostgreSQL with a test-only provider. These tests do not prove live Google claims,
deployed TLS/PKI, proxy configuration, retention or production readiness.
