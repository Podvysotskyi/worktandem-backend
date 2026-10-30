# Company onboarding, platform review and workspace access

This increment implements backend contracts for the merged
[readiness design](https://github.com/Podvysotskyi/worktandem-docs/blob/f9acaa0e0b596ee452e5daa0e7fa75e4c9e2e70f/docs/implementation/readiness.md).
It follows merged authentication PR #6. No frontend form, platform UI or deployed
Google/mutual-TLS integration is included.

## Browser integration

Expose Identity and Organization through the same HTTPS origin using explicit gateway/ingress
routes. The [Gateway transport](gateway.md) exposes `/api/identity` and `/api/organization`
prefixes; the endpoint paths below are the downstream paths after prefix removal.
Forward the opaque `__Host-wt-browser` cookie and explicit per-tab `X-WT-Account-Session` selector.
Never send account IDs, roles, email or platform capabilities as authority headers.
Organization gets the current actor from Identity on every protected request; its own
antiforgery principal is only the browser-container digest.

After Identity sign-in, GET `/companies/context` with that session selector. It returns
`applications`, accessible `workspaces` and `csrfToken`. It does not silently select an account
or company. Unsafe Organization requests require this token in `X-WT-CSRF` and the same
selector, alongside the browser cookie and Organization's separate `__Host-wt-org-csrf` cookie.
Identity routes continue using their own `/auth/context` token. Both token protocols preserve
independent tabs; neither has a browser-wide current-account field. No credentialed CORS is enabled.
All these responses are no-store. Missing HTTPS/runtime configuration fails closed.

| Organization endpoint | Contract |
| --- | --- |
| GET /companies/context | Current actor's own application statuses and currently usable memberships/workspaces; Organization CSRF token |
| POST /companies/applications | `{ operationId, form }`, `Idempotency-Key`, CSRF; returns 201 Pending snapshot and company ETag |
| GET /companies/applications/by-operation/{operationId} | Applicant-only resumable current status; foreign/absent operation returns 404 |
| POST /companies/{companyId}/applications | New form after Rejected only, company `If-Match`, `Idempotency-Key`, CSRF; returns 201 new immutable snapshot |
| GET /platform/company-applications?offset=0&limit=25 | Current `platform.companies.read` and fresh verified assurance; Pending queue ordered by creation/id; limit 1–100, offset 0–100000 |
| GET /platform/companies/{companyId}/application | Same operator read authority; current application plus applicant ID and separately authorized internal note |
| POST /platform/companies/{companyId}/applications/{applicationId}/decision | `{ outcome, applicationVersion, applicantVisibleReason, internalNote }`, company `If-Match`, `Idempotency-Key`, CSRF; current review capability and fresh assurance |
| GET /companies/{companyId}/workspace | Matching `X-WT-Company`; live account/session, Approved company, Active membership and company-scoped `company.workspace.read` assignment |

`outcome` uses JSON numeric enum values 0=Approved, 1=Rejected. Unknown values are rejected.
ETags are quoted opaque UUID versions: `If-Match: "<companyVersion>"`. Missing/malformed
precondition returns 428; stale company or application version returns 412. The applicant
projection never includes internal notes. Applicant-visible rejection reason is required,
trimmed and bounded to 2000 characters; internal notes are independently bounded to 2000.

Required form fields are `companyName` (trimmed 1–200), `countryCode` (ISO alpha-2),
`businessDescription` (20–2000), `applicantRoleDescription` (2–200). Optional `website`
is an absolute HTTP(S) URI without userinfo, max 2048; `legalRegistrationId` max 200.
These fields do not verify legal existence, website/domain ownership or eligibility.
No automatic domain joining or duplicate-name merging is performed. Slugs are generated
from the new company UUID and are not client authority.

## Resumption, concurrency and local transactions

Normal registration's stable `InitialRegistrationOperationId` comes from Identity's current
authenticated authority response. An arbitrary client replacement returns 409. The bootstrap
owner has no mandatory company or initial operation; POST Identity `/account/company-operation`
with its selected session/Identity CSRF issues one separate stable optional operation. Ordinary
accounts receive their existing initial operation from the same route. No additional-company
creation workflow is exposed in this increment.

Organization validates Identity immediately before its transaction, locks the actor's creation operation, and
durably enforces unique `(CreatedByAccountId, CreationOperationId)`. The immutable first snapshot
retains the normalized creation payload. Even after HTTP receipts expire, a lost-response retry
with that operation and unchanged payload returns the existing company's current status; changed
creation payload returns 409 and must use the rejected-application resubmission route.
An Identity account survives unavailable Organization with no company privileges; retry/query
uses the same account/operation rather than deleting/recreating its Google identity.

Each write receipt is actor + operation + target + key + payload bound, retained seven days.
Same key/payload replays the original result; changed payload conflicts. Current authority is
required before replay. Expired receipts are removed on use; deployment retention cleanup is
still an operational prerequisite. Durable creation uniqueness never expires with receipts.

Submission commits Pending company, immutable application, pointer/version and immutable audit
in one Organization transaction. The candidate is the application's applicant ID; no membership
exists yet. Rejection retains the snapshot/decision and creates no membership. Resubmission
requires Rejected and current company version, creates a new numbered snapshot, updates the
current pointer and advances Company.Version.

Approval obtains fresh request-bound Identity review authority before the local transaction,
then serializes on the company row, prohibits applicant/creator self-review, and atomically verifies
Pending + current application ID + expected company/application versions + no prior decision.
It commits Approved, decision/evidence, the applicant's one Active membership, a company-local
system Owner role, its explicit workspace permission/assignment, immutable audit and receipt.
The initial Owner role currently grants only the implemented workspace-read permission; it is
not a wildcard and does not silently grant future modules. Concurrent duplicate decisions have
one effect. Bounded retries cover known PostgreSQL deadlock/serialization and EF concurrency
failures; each known retry obtains fresh Identity authority before beginning its new local
transaction. No network calls occur inside Organization transactions. Evidence older than ten
seconds after local lock waits, or past session expiry, fails closed and requires a new request.
Uncertain commits are resolved using the same idempotency key, never blindly retried.

Membership/role/company status writers are not publicly exposed. Future writers must acquire
the same company aggregate lock, advance applicable versions and preserve ownership policy.
Workspace reads still check current stored company/membership/permission state. A Pending or
Rejected application never disables the actor's unrelated approved memberships. Platform grants
never imply a company membership, including for the bootstrap owner.

## Minimal reviewer provisioning in Identity

An eligible current `platform.owner` with explicit `platform.grants.manage` and fresh verified
MFA can assign/revoke only `platform.companies.read` and `platform.companies.review` for a
different registered Active account. No company membership is needed. The target is an explicit
account UUID, never email lookup/merging; the administrator must get it from the registered
account's own authenticated context. General user directory/UI is separate work.

- GET `/platform/reviewer-grants/{targetAccountId}` returns the two capability assignments,
  active state and versions under the same owner/assurance checks.
- POST `/platform/reviewer-grants` requires Identity CSRF, selected session, `Idempotency-Key`
  and `{ targetAccountId, capability, active, expectedVersion }`. Use null version only for
  absence; an existing assignment requires its exact version even when revoked. Returns the
  resulting version. Seven-day actor/payload-bound receipts support uncertain-delivery replay.

Grant changes serialize in Identity, check current actor/target/grants/assurance under locks,
and atomically persist assignment, target/capability/prior/result-version audit and receipt.
This command cannot grant Owner, grant-management, global status or recovery powers. No grant
or status is taken from the public chooser. Revocation takes effect on the next authority call.

## Authenticated service boundary and deployment gates

Organization's client uses only the configured Identity HTTPS origin, normal server
chain/hostname/revocation validation and its existing workload client certificate. Redirects,
cookie storage and HTTP-client logging are disabled. It requests a new evidence ID for each
call and checks request ID, intended `organization` service, exact action, account/session
binding and short response freshness. No positive authorization cache or shared EF model exists.
An unavailable/malformed/stale authority response returns safe 503; Identity's denied account
or capability returns 401/403. No account/tenant headers, old evidence or client permission
strings can replace this exchange.

Set separate Organization configuration only through controlled deployment:

- `ConnectionStrings__Organization`
- `Worktandem__Identity__Origin`: trusted HTTPS origin, no path/query/userinfo
- `Worktandem__Identity__ClientCertificatePath` and optional `ClientCertificatePassword`
- Organization `Worktandem__DataProtection__KeysPath`, `CertificatePath`, optional `CertificatePassword`
- Matching Identity `Worktandem__Internal__OrganizationCertificateSha256` and trusted workload PKI

The Organization key ring uses a different application discriminator and antiforgery cookie
from Identity. Both require pre-existing valid private-key certificates and protected storage.
No certificates, key rings, credentials, client grants, database roles or deployment are provisioned.
The actual Google claim assurance gate still defaults closed and must be verified with the real
client before privileged rollout. In-flight work admitted before a later revocation may finish;
the two databases have no distributed authorization lock or shared transaction.

## Validation and remaining work

Integration tests use disposable PostgreSQL 18.6 databases, the actual framework OIDC handler
with isolated metadata/backchannel, and Organization's actual HTTP authority client forwarding
to Identity's actual certificate handler. TestServer injects only a test peer-certificate feature
and an in-process test root; this does not prove a deployed TLS handshake or change machine trust.
Tests cover creation retry/races, rejection/resubmission, superseded/stale/duplicate decisions,
rollback, independent owner activation, tenant/session isolation, revoked grants, assurance and
authority failure, stored suspension/permission denial and unrelated memberships.

Nuxt forms/status/platform queue and selector/cache UX consume these contracts through the
Gateway; their validation is owned by the frontend repository. Invitations/mailbox proof,
directory sync, optional Google APIs, CRM modules, public global
status/removal, ownership recovery and last-owner mutation policy remain closed. Live Google,
PKI/ingress, retention/backups and production readiness are not claimed.
