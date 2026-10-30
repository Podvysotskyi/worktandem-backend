# Google OIDC and temporary login proof

This is the first bounded part of [readiness PR2](https://github.com/Podvysotskyi/worktandem-docs/blob/f9acaa0e0b596ee452e5daa0e7fa75e4c9e2e70f/docs/implementation/readiness.md).
It stops at browser-bound PendingLogin proof. It does **not** create a permanent account,
grant, company or usable AccountSession, complete bootstrap, or enable privileged actions.

## Implemented HTTP contract

| Endpoint | Behavior |
| --- | --- |
| GET /auth/context | Initializes a Secure/HttpOnly/SameSite=Lax __Host-wt-browser credential and framework antiforgery cookie/token. No account or database mutation. Response is no-store |
| POST /auth/start | Requires HTTPS, same-origin context and X-WT-CSRF. Accepts purpose Bootstrap/Login/Register/AddAccount and a fixed returnPath of /, /setup or /auth/continue. Starts framework OIDC with state/nonce/correlation and S256 PKCE |
| GET /signin-google | Framework callback only. Validates signed RS256 provider response, issuer/audience/lifetime and protocol proofs; records temporary evidence and redirects to the fixed destination with a non-bearer pendingLogin identifier |
| GET /auth/pending/{id} | Returns only this browser's unexpired flow ID, purpose, expiry and proof-validation flag; foreign/expired/missing rows return 404 |

The identifier alone never grants authority. There is no public proof-ingestion endpoint or
production simulator. Only the framework's post-validation TicketReceived adapter can submit
provider evidence. The scheme's fallback SignIn/SignOut handler rejects accidental cookie sign-in.

Ordinary Login/Register/AddAccount starts require completed setup. Bootstrap start requires
incomplete setup with no permanent accounts. Starting/validating a flow never inserts accounts.
The real account-creation gate must still be rechecked under the singleton lock by the later
completion workflow; this transport check is not a substitute.

## Container and request isolation

The browser gets an opaque random 256-bit credential; the database stores only its SHA-256 digest.
Pre-login cookie initialization does not persist a database row; the protected start POST
creates/reuses a browser row and a pending flow. Browser rows have a 12-hour absolute lifetime.
Expired/revoked browser records cannot start or read a flow.

A stable container principal exists solely for framework antiforgery. It is **not** an authenticated
Worktandem actor. The default authorization policy denies all routes that merely call
RequireAuthorization; later protected routes need explicit current-account policies.
No role/account/company claims are supplied by this principal.

Framework antiforgery additional data binds each request token to X-WT-Account-Session when
provided, or the prelogin purpose when absent. Tokens from concurrent tabs retain the same
container cookie and independent selector binding. In this subset selectors are untrusted hints;
no account-session ownership or authorized account writes exist yet. Tests demonstrate concurrent
pre-login starts, not completed two-account business writes. That acceptance test remains PR2/PR3 work.

Origin/Fetch Metadata checks supplement antiforgery on POST. No CORS permission is enabled.
Google's GET callback is the framework-protected protocol exception, never a GET business command.

## Persistence adjustment

Identity's new pending_logins table stores flow purpose/return destination, BrowserSession FK,
creation/expiry, concurrency version and optional validated issuer/subject/email/hd observations,
provider auth_time and an observed MFA flag. There is no Google code, state, nonce, access token,
ID token, client secret, setup secret or raw browser credential column. Framework state/nonce
and correlation use protected protocol data/cookies, rather than a parallel crypto implementation.

Awaiting flows expire after five minutes. Successful framework validation gives the pending
proof five minutes from validation. A local row lock and version prevent a second validation,
including competing replicas. A shared browser transaction lock limits unexpired flows to five;
excess starts return 429 with Retry-After. This is a per-browser flow limit, **not** the future
shared bootstrap-secret attempt/IP/deployment budgets.

Validated means provider-protocol validation succeeded, not that E12 privileged assurance is
satisfied. Bootstrap asks Google for auth_time/amr and max_age=300, but returned claims are only
observations here. Missing/stale MFA cannot yield privileges because completion remains absent.
The later workflow must revalidate same-identity assurance, age, secret and all bootstrap invariants.
Google's actual optional-claim availability remains unverified.

Expired rows grant no authority even before cleanup. A reviewed retention/cleanup operation for
pending provider evidence and expired browser containers remains required before production use.
It must never touch permanent SetupState or account/audit history.

## Runtime configuration and live integration gate

Auth routes return 503 unless all server-only runtime configuration is usable:

- ConnectionStrings__Identity.
- Worktandem__Google__ClientId and Worktandem__Google__ClientSecret.
- Worktandem__DataProtection__KeysPath: existing absolute shared key-ring directory.
- Worktandem__DataProtection__CertificatePath and optional CertificatePassword:
  an existing valid PFX with private key, used to protect the persisted key ring.

The application creates no deployment credential, OAuth client, certificate or Google grant.
No alternate issuer/authority/token endpoint can be configured through these settings. Google
authority/discovery and accepted issuer aliases are fixed; subject matching remains case-sensitive.
Do not place credentials in browser configuration or shell history. API startup never applies migrations.

Framework request-URL logging is disabled because callback URLs contain codes/state. Framework
authentication payload diagnostics are disabled; safe problem responses contain only stable codes
and a correlation ID. Upstream proxy/access logs must also redact callback queries during deployment.
This backend does not configure external ingress or claim its logs are safe.

## Verification and remaining PR2 work

Tests use the actual ASP.NET OpenID Connect handler with a test-host-only HTTPS backchannel,
ephemeral signing/Data Protection keys and disposable PostgreSQL 18.6. The simulator verifies the
client and PKCE exchange and redeems a code once. Production has no simulator-selection switch.

Coverage includes valid proof without permanent authority; wrong issuer, audience, signature,
expiry, nonce/state/correlation; replay and competing validation; cross-browser access; fixed
redirects; selector-bound CSRF and concurrent prelogin tabs; pre-bootstrap registration closure;
flow expiry/limit; missing configuration; minimal/redacted responses/logs; migration replay and
the existing foundation tests.

Remaining before PR2 is complete: validated-proof consumption and audit, shared setup attempt budgets,
constant-time environment-secret verification, live same-identity E12 freshness checks, serialized
first-owner account/identity/grants/setup/session transaction and rollback/races, normal registration
operation issuance, runtime account-session creation/selection/revocation/idle limits, logout,
and authenticated internal authority contracts. No bootstrap-complete, session-context, registration-
complete, grant/status write or protected business endpoint is enabled by this subset.

Live Google client/redirect integration, protected shared key-ring provisioning and actual E12
claim evidence are separate prerequisites. Simulator tests do not establish live Google E2E,
working account login, operator readiness or production readiness.
