# PostgreSQL persistence foundation

## Baseline and design dependency

This first slice follows [docs PR #7](https://github.com/Podvysotskyi/worktandem-docs/pull/7)
merged at [f9acaa0e](https://github.com/Podvysotskyi/worktandem-docs/blob/f9acaa0e0b596ee452e5daa0e7fa75e4c9e2e70f/docs/implementation/readiness.md).

Verified 2026-10-29: [PostgreSQL 18.6](https://www.postgresql.org/support/versioning/),
[EF Core 10.0.14](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore/10.0.14),
[EF tooling 10.0.14](https://www.nuget.org/packages/dotnet-ef/10.0.14),
[Npgsql EF 10.0.3](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3)
and [driver 10.0.3](https://www.nuget.org/packages/Npgsql/10.0.3).
The provider permits EF >=10.0.4 and <11; the chosen patch fits. Both providers explicitly
target PG18 SQL compatibility. Official packages restore successfully; per-project lock files
record the graph. The fixture and CI run the official postgres:18.6 image. No frontend/runtime
pins or unrelated dependencies change.

## Implemented schema

IdentityDbContext owns accounts, external_identities, setup_state, platform_grants,
browser_sessions, account_sessions and account_status_transitions.
The subsequent [bounded authentication subset](authentication.md) adds pending_logins for
browser-bound temporary flow/proof evidence; its migration changes no Organization tables.
The completion increment adds consumed proof, session assurance, account authentication cutoff,
one live session per account/container, immutable authentication_audit and transient setup_attempts.

- Case-sensitive canonical issuer + subject is globally unique. Current scope requires
  Google and canonical https://accounts.google.com. A partial unique AccountId index applies
  only to Google; it does not decide future-provider cardinality. Email is mutable evidence,
  not an account identity key. Exactly one login per active-account creation is a future
  workflow invariant; storage enforces at most one.
- AccountSession's composite FK binds its identity to the same account. Browser credentials
  have only a unique SHA-256 hex digest field; raw credentials/tokens/secrets have no columns.
  Runtime credential generation/verification and expiry/revocation enforcement arrive with auth.
- SetupState has exactly the permitted singleton key, completion-field checks and one
  incomplete migration seed. Triggers prohibit deleting/truncating the marker or changing
  completed state. The historical initial-owner FK restricts physical deletion; controlled
  retention/recovery is later policy. No owner/secret is seeded.
- Status history enforces bounded state transitions and nonempty trimmed suspension reasons.
  History is append-only. Atomic account/status/history/session updates and all account-creation
  gates require the later use case; direct DbContext access is not an authorized workflow.

OrganizationDbContext owns companies, company_applications, review_decisions,
company_memberships, company_roles, role_permissions and company_role_assignments.

- Durable creator + creation-operation uniqueness supports resumable registration independently
  of future short-lived HTTP idempotency records.
- Company current-application pointer, reviewed application/version, role assignments and
  role permissions carry CompanyId in their local FKs. A cross-company reference fails in SQL.
- One terminal decision per application, positive submission number and unique company/submission
  number are enforced. Rejection requires applicant-visible reason. Applications and reviews
  are immutable snapshots with update/delete/truncate guards; resubmission inserts a new record.
- Company/account membership is unique. Membership status/end dates and bounded company states
  are checked. Account IDs and reviewer authority references have no cross-service SQL FK.
- Owner candidate is the application's applicant. No candidate membership/role is seeded.
  Only company-wide RBAC is introduced for the initial activation path; other scope tables
  and permission catalogs wait for their actual use cases. Unknown permissions must deny later.

All tables/columns/keys/indexes use service-local snake_case mapping, timestamp with time zone
for UTC instants and restrictive local deletion. PostgreSQL identifier length is handled with
stable name hashes. Entity creation paths will issue UUIDv7 IDs; constructors accept explicit
IDs for persistence/reconstitution. Migrations supply no random entity seed. Guid Version
properties are concurrency tokens; Account.Rename demonstrates atomic version advancement.
Later mutable use cases must advance their aggregate versions, use If-Match and authorize writes.
Keys and EF version predicates do not enforce authorization, self-review prevention, approval
state machines or cross-service ownership invariants.

## Controlled migration tooling

Restore the local tool with dotnet tool restore. Migration source/snapshot generation needs
no database or credential. Examples from the repository root:

```sh
dotnet ef migrations list --project src/Worktandem.Identity.Infrastructure
dotnet ef migrations list --project src/Worktandem.Organization.Infrastructure
dotnet ef migrations script --idempotent --project src/Worktandem.Identity.Infrastructure --output artifacts/identity.sql
dotnet ef migrations script --idempotent --project src/Worktandem.Organization.Infrastructure --output artifacts/organization.sql
```

For an explicitly provisioned development/test target, supply ConnectionStrings__Identity
or ConnectionStrings__Organization through environment variables outside shell history and Git,
then run dotnet ef database update --project src/Worktandem.<Service>.Infrastructure.
The design-time factories read only their respective variable; absent configuration can generate
scripts but cannot apply migrations. API hosts also support normal ASP.NET configuration providers.
Never aim these commands at existing data without a reviewed deployment/backup procedure.
No database update is part of API startup. Production roles and a separate migration identity
remain deployment work.

Each service has an initial schema migration and a history-protection upgrade. Down migrations
exist for empty disposable development environments; they destroy state and must never be used
as an automatic production rollback or way to reopen setup. Deploy a forward repair instead.

## Verification and limits

The real PostgreSQL suite applies initial then history migrations on empty task-owned databases,
repeats migration application and replays generated idempotent scripts. It checks model/snapshot
consistency, Google/tenant/registration keys, constrained session identity, optimistic lost-update
failure, reason/history integrity, first-owner row-lock contention and rollback, permanent setup
closure and readiness with/without migrations. Fixtures produce only synthetic records and use
no Google client or live bootstrap secret.

The authentication suite adds actual framework OIDC, bootstrap, account-session and certificate
handler tests against this database. It does not establish live Google/MFA or deployed TLS,
company authorization, invitation acceptance or production database permission separation.
Company and public global-status workflows remain absent.
