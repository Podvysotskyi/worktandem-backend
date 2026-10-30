# Backend architecture

## Current implementation

ASP.NET Core on .NET 10 with independent Identity, Organization and Gateway executables.
Identity and Organization each follow:

```text
Api -> Application -> Domain
Api -> Infrastructure -> Application
```

Domain contains framework-free entities/enums for the first persistence slice, with explicit
IDs, UTC instants and Guid versions. Application owns the actual authentication use cases and ports; it has
no fake identity/authorization services or generic repository framework. Infrastructure maps
its own domain and owns Npgsql migrations/snapshots. Api composes persistence and probes.
No persistence entities are HTTP DTOs; no business HTTP contract is enabled.
The [authentication workflows](authentication.md) use Application-owned pending-login and
bootstrap/session workflows, explicit transactional PostgreSQL adapters, and framework OIDC,
antiforgery and certificate-authentication HTTP adapters. The stable browser principal never supplies
account authority; selected sessions are independently validated from current Identity state.

Neither service references the other's projects or SQL objects. AccountId fields in Organization
are external identifiers only. Gateway has no domain layers, tables, direct DbContext or service
implementation references. Identity exposes a certificate-authenticated authority contract;
Organization's later protected use cases must call it and enforce their own company/RBAC state.

Architecture tests assert both service graphs and package-free inner layers. Source and transitive
dependencies must also be reviewed; project-reference tests do not prove semantic authorization.

## Storage boundaries

[Persistence details](persistence.md) describe separate PostgreSQL databases, tenant composite
foreign keys, partial Google uniqueness, immutable history, restrictive deletion and optimistic
concurrency. No distributed transaction or cross-database relationship exists. The startup hosts
never create or migrate schemas. Migration deployment is a controlled job with its own credentials.

Company approval/candidate activation, no self-review, account/session eligibility, complete
permission/scope evaluation and last-owner protection are future application workflows. Checks
and keys constrain stored shape; they do not establish that a caller is authorized. There are
no EF global query filters presented as access control.

Only company-scoped roles/assignments are mapped for the initial owner-activation path.
Own/department/team scoped roles, organizational structures, invitations, directory integration
and CRM modules stay deferred. Platform grants in Identity never imply a company membership.

## Operational and security contract

Liveness asserts the process can respond. Identity/Organization readiness checks connection and
pending migrations; missing configuration/database/schema fails closed with 503. Gateway readiness
remains process-only. OpenAPI is development-only. Identity has a configured Google callback and
opaque browser-container/antiforgery cookies, protected bootstrap and server-side account sessions.
There are no company/review business operations or public global status writes.

Before enabling any protected operation, implement independently enforced current Identity
status/session validation, Organization company/membership/scope checks, negative cross-tenant
tests, CSRF/session protocol and authenticated internal transport. No caller-supplied tenant or
actor header can become authority. Pending companies grant no business access, and global versus
membership suspension remain independent.

The actual first-owner workflow is tested end to end with the framework OIDC simulator, secret,
proof/assurance gates, shared budgets and PostgreSQL rollback/concurrency tests.
Privileged assurance still requires evidence from the actual Google client before its deployment gate
is enabled. Recovery/last-owner policy and invitation mailbox proof remain gated.

Trusted proxy configuration, allowed hosts, durable Data Protection, runtime roles, telemetry,
secrets and deployment are still environment decisions. This foundation is not production readiness.
