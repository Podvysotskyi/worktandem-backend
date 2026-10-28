# Worktandem backend

Initial **.NET 10 LTS / ASP.NET Core multi-service scaffold**, not a production application.

## Run locally

Install a supported .NET 10 SDK (see `global.json`). From the repository root:

```sh
dotnet restore Worktandem.slnx
dotnet build Worktandem.slnx --configuration Release --no-restore
dotnet test Worktandem.slnx --configuration Release --no-build
```

Start each independent executable in a separate terminal:

```sh
dotnet run --project src/Worktandem.Organization.Api -- --urls http://localhost:5101 --environment Development
dotnet run --project src/Worktandem.Gateway -- --urls http://localhost:5100 --environment Development
```

Each host exposes `/health/live` and `/health/ready`. Both return plain `Healthy`
when the host is running. Readiness currently has **no external dependency checks**,
because neither service has external dependencies. It is not evidence of database,
identity provider, or downstream availability. Add tagged checks when these exist.
Development only: `/openapi/v1.json` (document, not a Swagger UI).

Configuration uses normal ASP.NET Core configuration providers. `.env.example` is
reference documentation, not an automatically loaded configuration file. Use shell
environment variables or user-secrets for future local credentials; never commit secrets.
Default environment is Production; OpenAPI is not exposed there.

## Services

- `src/Worktandem.Organization.Domain`: future Organization & Access business rules (framework-free).
- `src/Worktandem.Organization.Application`: future use cases and ports; depends only on Domain.
- `src/Worktandem.Organization.Infrastructure`: future adapters and service-owned migrations; depends on Application.
- `src/Worktandem.Organization.Api`: executable HTTP host and composition root.
- `src/Worktandem.Gateway`: thin future BFF host; no proxying, data store, or business logic yet.
- `tests/Worktandem.SmokeTests`: in-memory HTTP tests for both hosts plus executable project-dependency rules.

**Authentication and authorization are not implemented.** There are deliberately no
organization, membership, role, or other business endpoints. Health endpoints are
anonymous operational probes. Do not expose this scaffold as a production application.
No authentication provider, database, broker, orchestration stack, or RBAC engine is selected.

Publish/deploy the Api and Gateway hosts independently with `dotnet publish <project> -c Release`.
Container packaging is deferred until it can be built and tested.
See [architecture](docs/architecture.md) for boundaries and the next decisions.
