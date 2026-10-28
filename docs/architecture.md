# Initial backend architecture

## Decision

Use ASP.NET Core on .NET 10 LTS with independently deployable service executables.
Keep the starting footprint small: Organization & Access ownership plus a Gateway/BFF
host. Do not add speculative services or distributed infrastructure.

## Boundaries

| Host | Owns now | Planned responsibility |
| --- | --- | --- |
| Organization | Process, health, development OpenAPI | Organizations, memberships, scoped roles and access policy |
| Gateway | Process, health, development OpenAPI | UI-facing composition and routing, not domain ownership |

Neither service references the other. The test harness references both only to host
them in isolation. Future communication must use explicit API/event contracts, not
shared persistence entities, cross-service SQL, or a shared DbContext. Each service
owns its schema and migrations. `Worktandem.Organization.Infrastructure/Migrations` reserves that boundary;
there is no database or migration execution today. The Gateway should remain stateless.

## Clean Architecture inside Organization

Dependencies point inward:

```text
Api -> Application -> Domain
Api -> Infrastructure -> Application
Gateway (separate executable, no Organization project references)
```

- **Domain**: business invariants, value objects and entities once needed. No HTTP,
  persistence, messaging, framework packages, or application-layer references.
- **Application**: use cases, application contracts and ports required by those use
  cases. Depends only on Domain, not ASP.NET Core or infrastructure implementations.
- **Infrastructure**: adapters implementing application ports; owns persistence,
  migrations and external integrations when selected. Domain is transitively available
  through Application. Do not expose persistence entities as API contracts.
- **Api**: transport DTOs/endpoints, request mapping and composition root. Its reference
  to Infrastructure exists for dependency registration, not to bypass application use cases.

The layers are intentionally empty until the first authorized business use case.
There are no invented entities, generic repositories, mediator/event-bus libraries,
identity stubs, or database dependencies. Add abstractions only at actual boundaries.
Gateway remains a thin BFF rather than receiving artificial domain layers.

`ArchitectureTests` assert the allowed project-reference graph and framework-free
inner project definitions, alongside host smoke tests. These are initial guardrails,
not proof of semantic separation: review future source code, shared build props and
transitive dependencies too. Introduce targeted business-rule/unit tests alongside
real use cases, adapter integration tests with real dependencies, and denied-access
HTTP tests before implementing protected operations.

## Security before features

Select an identity provider and token/session model first. Authentication must validate
issuer, audience, signature, expiry, and intended client flow. Backend services must
enforce authorization themselves, not trust the Gateway as their only security boundary.
Plan organization-scoped RBAC: the authenticated principal's membership, organization
scope, and permission must be checked for every protected operation. Never trust a
client-supplied organization ID or role alone. No policy engine or mock identity exists.
Do not add business routes before these controls and negative authorization tests exist.

## Operational contract

Liveness is process-only. Readiness presently means this dependency-free host can
serve requests. Add actual dependency checks, startup/migration strategy, timeouts,
and failure-mode tests as persistence/downstream calls are introduced. Keep public
probe responses minimal; restrict probe access at deployment ingress if needed.
OpenAPI documents are development-only. HTTPS termination, trusted forwarded headers,
allowed hosts, telemetry/export, secrets, deployment infrastructure, and CI hardening
remain deployment decisions; this scaffold makes no production-readiness claim.

## Next decisions

1. Identity provider, login flow and organization-scoped authorization contract.
2. First organization use case and its API contract, including denied-access tests.
3. Organization-owned persistence and migration tooling.
4. Implement Gateway routing/composition only for an actual client need.
5. Verify container packaging and deployment probes in the chosen runtime.

.NET support reference: https://dotnet.microsoft.com/en-us/platform/support/policy
