# Worktandem backend

.NET 10 / ASP.NET Core persistence foundation. Authentication, authorization and business
workflows are not implemented. Only operational probes and development OpenAPI are exposed.

The scope follows [implementation-readiness docs PR #7](https://github.com/Podvysotskyi/worktandem-docs/pull/7)
([immutable design](https://github.com/Podvysotskyi/worktandem-docs/blob/b2cd99e1a6596dfcd3c3efd2e9fe1e6e28390bca/docs/implementation/readiness.md)).
That design is an unmerged dependency at this change's preparation.

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
  Application has no use cases yet; Infrastructure owns its context and migration sources.
- Gateway remains a separate stateless BFF scaffold with no persistence/service references.

There are no Google login, setup, session, company, review, status-write or invitation endpoints.
Do not expose this foundation as a working production application. The next slice implements
validated Google OIDC, temporary pending login, bootstrap and server sessions with negative
security tests. Privileged assurance, recovery/last-owner coordination and invitation mailbox
proof remain gated by the design.

Publish/deploy executable hosts independently using dotnet publish <project> -c Release.
Container packaging and production deployment remain separate work.
See [architecture](docs/architecture.md).
