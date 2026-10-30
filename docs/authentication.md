# Google authentication, protected bootstrap and server sessions

The backend authentication workflows in [readiness PR2](https://github.com/Podvysotskyi/worktandem-docs/blob/f9acaa0e0b596ee452e5daa0e7fa75e4c9e2e70f/docs/implementation/readiness.md)
are implemented and tested with an isolated provider and PostgreSQL. Live Google client/claims,
deployment certificates, ingress and shared-key configuration remain unverified prerequisites.
Backend [company onboarding/review](company-onboarding.md) consumes these contracts;
the [Gateway](gateway.md) exposes the browser HTTP contracts with explicit service prefixes.

## HTTP contracts

All browser routes below require configured authentication runtime, HTTPS and no-store responses.

| Endpoint | Contract |
| --- | --- |
| GET /auth/context | Initializes an opaque Secure/HttpOnly/SameSite=Lax __Host-wt-browser cookie and framework antiforgery cookie/token; no account/database mutation |
| POST /auth/start | X-WT-CSRF; purpose Bootstrap/Login/Register/AddAccount and returnPath /, /setup or /auth/continue; framework code flow with state/nonce/correlation and S256 PKCE |
| GET /signin-google | Framework callback only; validates signed RS256 response, issuer/audience/lifetime/protocol proof, records temporary evidence, redirects with non-bearer pendingLogin ID |
| GET /auth/pending/{id} | This browser's unexpired/unconsumed flow summary only; foreign/expired/consumed records return 404 |
| POST /setup/complete | X-WT-CSRF plus pendingLoginId and entered secret; one-time first-owner transaction, returns 201 and account-session reference |
| POST /auth/complete | X-WT-CSRF plus pendingLoginId; displayName (trimmed 1–200) required only for a new Register/AddAccount actor; returns its own account-session context |
| GET /session/context | Without selector: safe session chooser and prelogin CSRF token, selected=null. X-WT-Account-Session selects only that cookie-owned active account; returns selected context and selector-bound CSRF |
| POST /session/logout-current | Valid CSRF + explicit account-session selector; revokes only that session, preserving other accounts and browsers |
| POST /session/logout-container | Valid CSRF; revokes every account session in this browser and the container, clears its browser/antiforgery cookies; other browsers remain independent |

No business mutation uses GET. Cookie/token initialization and selected-session idle renewal are
operational session behavior; the framework-validated Google callback is the protocol GET exception.

Google issuer aliases are canonicalized to https://accounts.google.com; subject is case-sensitive.
Email never resolves, merges or transfers an account. Existing issuer/subject resolves its own account;
a different identity creates/resolves that identity's separate account, never a second link.
Login of an unknown identity requires a new Register/AddAccount flow.

Normal creation is closed before setup. After setup, a new normal account receives one server-issued
InitialRegistrationOperationId, retained across later sign-ins. It receives no company or platform
grant. Organization's creation handler obtains/validates that operation through the
authenticated authority contract rather than trusting a client-supplied replacement.
The bootstrap owner has no initial registration operation or mandatory company membership.
An existing actor can start Login with requireFreshMfa=true to request Google's fresh amr/auth_time
evidence and max_age=300 before replacing its session. The request flag grants nothing by itself;
the returned signed evidence must still pass the same server assurance checks.

## Bootstrap transaction and shared budgets

The server reads **only** Worktandem__Bootstrap__Secret for setup authority; JSON/browser configuration
cannot supply it. It must be a separately supplied high-entropy value of 32–4096 characters.
The adapter compares fixed-size SHA-256 digests with CryptographicOperations.FixedTimeEquals and
retains neither entered/configured secrets nor their hashes in database records, URLs, audit or logs.

The deployment must explicitly set Worktandem__Google__PrivilegedAssuranceVerified=true only after
the real client supplies verified amr/auth_time evidence. This gate is not proof by itself:
bootstrap additionally requires the same validated identity's MFA observation and auth_time within
five minutes, not in the future beyond bounded skew. Missing/stale evidence fails closed, even
with the right setup secret. No fixture identity or alternate provider switch exists in production.

Shared PostgreSQL rolling windows allow at most five failed/reserved attempts per flow and source
IP in ten minutes, plus thirty total setup reservations per minute across replicas. Only a source-IP
digest is stored. A deployment-wide transaction lock serializes reservations; failures remain
committed independently of the main workflow rollback. Successful completion marks its reservation
successful in the owner transaction. 429 includes a conservative Retry-After upper bound.
Trusted proxy configuration is separate; no client-supplied forwarding/IP header is trusted here.

In one Identity transaction, bootstrap locks the durable setup singleton, proof and browser;
rechecks incomplete setup, zero accounts, fresh unused browser-owned proof, secret and assurance;
creates Account + its one Google identity + six explicit owner/admin capabilities; completes
SetupState, consumes proof, creates a session, and appends immutable authentication audit.
Intermediate saves remain within that transaction. Failure after an intermediate save rolls back
every owner/grant/session/setup/audit effect, while the attempt reservation remains.
Bounded transaction retries reload state after serialization/deadlock/concurrency failures.

Time is checked after relevant lock acquisition. A request waiting across proof, MFA or session
expiry cannot authorize using its arrival time. Completed setup never resets. Its historical
initial-owner Restrict FK retains a closed/anonymized account record; physical erasure/recovery
requires separately reviewed policy. Removing the deployed setup secret does not prevent ordinary
sign-in/registration and can never reopen setup.

## Browser, tabs, sessions and revocation

A random 256-bit opaque credential identifies a server browser container; only its digest is stored.
The stable container principal exists solely for framework antiforgery and grants no account or role
authority. Default authorization denies routes that merely require this principal.
Actual account operations independently resolve the selected session against its owning container.

Antiforgery additional data binds the request token to the explicit account-session selector or
prelogin purpose. Concurrent tabs retain a stable shared cookie and independent request tokens.
Origin/Fetch Metadata checks supplement unsafe-method CSRF validation; no credentialed CORS is enabled.
Account switching never sets a browser-wide current-account or current-company authority.

Account sessions expire after thirty minutes idle and twelve hours absolute, bounded by container
expiry. Only a valid selected session renews idle time. Signing in again for the same account in
one container revokes its prior session and creates a fresh reference; other accounts/containers
remain independent. Proof consumption is one-use. A lost completion response can be recovered through
that browser's safe chooser or fresh sign-in, not by replaying a consumed setup proof.

Every selected lookup checks current Account.Status and session/container revocation/expiry.
Public global suspend/reinstate/removal routes remain absent until ownership/recovery policy is agreed.
The trusted future status writer must atomically update status/history, call
Account.InvalidateAuthentication, and revoke all target-account sessions. Controlled PostgreSQL fixtures
exercise that transaction contract. Reinstatement never un-revokes sessions: both session authentication
time and proof creation/validation times must follow the account revocation cutoff.
A pre-suspension flow with a delayed callback cannot become a fresh post-reinstatement sign-in.

## Authenticated internal authority

POST /internal/identity/validate-session requires direct HTTPS client-certificate authentication,
normal chain/EKU/validity/revocation validation, and an exact server-configured SHA-256 pin at
Worktandem__Internal__OrganizationCertificateSha256. No forwarded certificate/actor header or
browser principal can authenticate this route. HTTPS listeners allow certificate negotiation;
the private route enforces the certificate, while browser routes do not require one.

The JSON request contains the forwarded opaque browserCredential, accountSessionId, action and
requestId. The raw credential is hashed in memory and never logged or persisted. Allowed actions
are organization.account, platform.companies.read and platform.companies.review.
The latter two additionally check the live explicit capability and current E12 assurance under
Identity's workflow locks. Unknown actions/callers and revoked/stale authority deny.

Response evidence binds a new evidence ID, request ID, service/action, actor/session/version and
validation time to this call. Organization must perform a new authenticated lookup for each protected
request and enforce its own tenant state; chooser/previous response capabilities are not reusable
authorization. Revocation after the authorization point can leave an admitted operation in flight.
No distributed lock or cross-database transaction is claimed.

## Runtime/deployment prerequisites

Auth routes remain 503 until client/database/protected-key-ring configuration is usable:

- ConnectionStrings__Identity; separate from Organization.
- Worktandem__Google__ClientId and ClientSecret for an existing Google client.
- Worktandem__DataProtection__KeysPath: existing absolute shared directory.
- Worktandem__DataProtection__CertificatePath and optional CertificatePassword: existing valid private-key PFX.
- Bootstrap additionally needs its environment-only secret and explicit assurance rollout gate above.
- Internal authority needs the Organization certificate pin, trusted client PKI and actual HTTPS listener/ingress setup.

No deployment credential, certificate, OAuth client or persistent Google grant is generated.
Actual redirect origin, optional assurance claims, certificate revocation availability and mutual TLS
must be verified in the deployment. Request-URL/auth payload logging is disabled because callback
queries contain codes/state; application problems/logs retain safe codes/correlation only.
External proxy logs still require deployment redaction.

The Identity migration adds consumed proof, session assurance and account revocation cutoff fields,
one active session per account/container, immutable authentication_audit and transient setup_attempts.
Only the incomplete setup marker is seeded. Expired proof/budget records grant no authority.
Retention/cleanup and backup recovery remain controlled deployment work; audit FKs deliberately retain
historical actor/browser references rather than cascading them away.

## Verification and remaining scope

Tests use the actual ASP.NET OIDC handler, test-only static provider metadata/backchannel/keys/clock,
ephemeral Data Protection, and disposable PostgreSQL 18.6. Certificate tests inject TestServer's peer
certificate feature and run the actual chain/EKU/pin handler against an in-process test root; they do
not prove a deployed TLS handshake or install machine trust.

Coverage includes concurrent first-owner setup, absent/wrong secret, replay/expiry, failure after
partial save and rollback, missing/stale MFA and unavailable rollout gate, rolling shared budgets,
multiple accounts/browsers, parallel tab CSRF writes, logout isolation, idle/absolute expiry, live
grant/status checks, reinstatement and delayed callback rejection, expiry during a held database lock,
certificate caller denial, request-bound authority, unavailable database, migration replay and audit guards.

This completes the backend bootstrap/session increment. The following company increment adds
backend approval/workspace access and a limited reviewer grant command described separately.
Frontend forms, grant UI, client cache/switching UX, invitations and unresolved owner/recovery
policies remain later slices. No live Google or production deployment is claimed.
