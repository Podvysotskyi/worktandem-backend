# Worktandem backend

.NET 10 / ASP.NET Core persistence, Google OIDC, protected bootstrap, server account sessions,
company application/review and workspace authorization.
The backend workflows are tested with an isolated provider; live Google and deployment integration
remain prerequisites. See [implemented authentication scope](docs/authentication.md).
See [company onboarding contracts and frontend integration](docs/company-onboarding.md).

The scope follows [implementation-readiness docs PR #7](https://github.com/Podvysotskyi/worktandem-docs/pull/7)
([merged immutable design](https://github.com/Podvysotskyi/worktandem-docs/blob/f9acaa0e0b596ee452e5daa0e7fa75e4c9e2e70f/docs/implementation/readiness.md)).
The persistence and authentication milestones are merged; this increment adds backend onboarding/review.

## Build and test

Install a supported .NET 10 SDK (global.json) and Docker with a Linux engine. From this repository:

```sh
dotnet tool restore
dotnet restore Worktandem.slnx --locked-mode
dotnet build Worktandem.slnx --configuration Release --no-restore
dotnet test Worktandem.slnx --configuration Release --no-build
```

The persistence suite requires Docker and pulls the official postgres:18.6 image. It creates
a uniquely labeled disposable container with random credentials and an OS-assigned loopback
port, creates isolated databases, and removes only its own container. It never accepts an
existing database connection. Docker unavailability fails the suite; tests are not silently
skipped. CI runs the same suite. Smoke/architecture tests can run independently:

```sh
dotnet test tests/Worktandem.SmokeTests --configuration Release
```

## Run hosts

Run independent executables in separate terminals, assigning different ports:

```sh
dotnet run --project src/Worktandem.Identity.Api -- --urls http://localhost:5102 --environment Development
dotnet run --project src/Worktandem.Organization.Api -- --urls http://localhost:5101 --environment Development
dotnet run --project src/Worktandem.Gateway -- --urls http://localhost:5100 --environment Development
```

These HTTP commands support probes/development OpenAPI. Authentication requires a separately
configured HTTPS listener and existing certificates/client configuration described in
[authentication](docs/authentication.md); this repository does not provision them.

All expose /health/live (process only). Identity/Organization /health/ready returns 503
without its service connection string, database connectivity or all migrations; it returns
200 after those checks pass. Gateway has no external dependencies and its readiness remains
process-only. Probes return no provider details. Readiness does not establish Google,
session, tenant authorization or production readiness. Development only: /openapi/v1.json.

Use normal ASP.NET configuration providers: ConnectionStrings__Identity and
ConnectionStrings__Organization select **separate databases and connection identities**.
.env.example documents keys and is not loaded automatically. No credentials are committed.
Provisioning runtime database roles, grants, secrets, backups and service transport is later
environment work. The integration harness's temporary postgres user is only for disposable tests.

## Persistence and migration operations

See [persistence](docs/persistence.md) for ownership, constraints, controlled migration
commands and test coverage. EF migrations are never run by API startup or health checks.
No real account, operator, Google subject, company or bootstrap secret is seeded.
Only Identity's incomplete setup singleton is seeded.

- Identity owns accounts, Google identity associations, browser/account sessions, platform
  grants and account status history.
- Organization owns company/application/review records, memberships and company-scoped RBAC.
- Each service has Domain, Application, Infrastructure and Api projects. Domain is framework-free;
  Application owns authentication or company use cases; Infrastructure owns its context and migration sources.
- Gateway remains a separate stateless BFF scaffold with no persistence/service references.

Identity exposes configured HTTPS sign-in/bootstrap completion, explicit account-session context,
logout, restricted reviewer provisioning and a certificate-authenticated internal authority contract.
Organization implements Pending application, independent review and explicit company workspace access.
No public status-write or invitation endpoint exists. Privileged bootstrap/authority require
validated fresh MFA plus the explicit deployment assurance gate.
Do not expose this as a production-ready application: real Google/PKI integration, deployment,
owner/recovery policy, gateway/UI binding and client switching/cache UX remain separate work.

Publish/deploy executable hosts independently using dotnet publish <project> -c Release.
Container packaging and production deployment remain separate work.
See [architecture](docs/architecture.md).
