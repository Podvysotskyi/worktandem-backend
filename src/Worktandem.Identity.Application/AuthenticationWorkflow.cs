using Worktandem.Identity.Domain;

namespace Worktandem.Identity.Application;

public sealed class AuthenticationWorkflow(IAuthenticationTransactions transactions, ISetupAttemptBudget budget,
    IBootstrapSecretVerifier secret, IPrivilegedAssuranceGate assurance, TimeProvider clock)
{
    public static readonly IReadOnlySet<string> PlatformCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "platform.owner", "platform.companies.read", "platform.companies.review",
        "platform.users.read", "platform.users.manage", "platform.grants.manage"
    };

    public async Task<SessionIdentity> Bootstrap(string digest, Guid proofId, string enteredSecret,
        string sourceHash, string correlationId, CancellationToken cancellationToken)
    {
        if (!secret.IsConfigured || !assurance.IsAvailable) throw Failure("setup_unavailable", 503);
        var attempt = await budget.Reserve(proofId, sourceHash, cancellationToken);
        return await transactions.Run(async (state, ct) =>
        {
            var setup = await state.LockSetup(ct) ?? throw Failure("setup_unavailable", 503);
            var proof = await state.LockProof(proofId, ct);
            var browser = await state.LockBrowser(digest, ct);
            var hasAccounts = await state.HasAccounts(ct);
            var now = clock.GetUtcNow();
            if (setup.Status != SetupStatus.Incomplete || hasAccounts ||
                !UsableProof(proof, browser, now) || proof!.Purpose != LoginPurpose.Bootstrap ||
                !secret.Matches(enteredSecret) || !Fresh(proof.MfaObserved, proof.ProviderAuthenticatedAt, proof.ValidatedAt, now))
                throw Failure("invalid_setup", 409);
            var owner = new Account(Guid.CreateVersion7(), "Platform owner", AccountStatus.Active, null, now, now, Guid.CreateVersion7());
            var identity = NewIdentity(owner.Id, proof, now);
            state.AddAccount(owner);
            state.AddIdentity(identity);
            foreach (var capability in PlatformCapabilities)
                state.AddGrant(new PlatformGrant(owner.Id, capability, null, now, null, null, Guid.CreateVersion7()));
            // The enclosing transaction also covers a failure after this first persistence step.
            await state.Save(ct);
            setup.Complete(owner.Id, now);
            proof.Consume(now);
            var session = await NewSession(state, owner, identity, browser!, proof, now, ct);
            await state.MarkAttemptSucceeded(attempt, ct);
            state.AddAudit(Audit("bootstrap.completed", owner.Id, browser!.Id, proof.Id, setup.Version, correlationId, now));
            return Identity(owner, session, PlatformCapabilities.ToArray());
        }, cancellationToken);
    }

    public Task<SessionIdentity> Complete(string digest, Guid proofId, string? displayName,
        string correlationId, CancellationToken cancellationToken) =>
        transactions.Run(async (state, ct) =>
        {
            var setup = await state.LockSetup(ct) ?? throw Failure("identity_unavailable", 503);
            if (setup.Status != SetupStatus.Completed) throw Failure("setup_required", 409);
            var proof = await state.LockProof(proofId, ct);
            var browser = await state.LockBrowser(digest, ct);
            var now = clock.GetUtcNow();
            if (!UsableProof(proof, browser, now) || proof!.Purpose == LoginPurpose.Bootstrap)
                throw Failure("invalid_login_proof", 409);
            var identity = await state.FindGoogle(proof.Issuer!, proof.Subject!, ct);
            Account account;
            var registered = identity is null;
            if (identity is null)
            {
                if (proof.Purpose == LoginPurpose.Login) throw Failure("registration_required", 409);
                if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200)
                    throw Failure("invalid_display_name", 422);
                account = new Account(Guid.CreateVersion7(), displayName.Trim(), AccountStatus.Active,
                    Guid.CreateVersion7(), now, now, Guid.CreateVersion7());
                identity = NewIdentity(account.Id, proof, now);
                state.AddAccount(account); state.AddIdentity(identity);
                await state.Save(ct);
            }
            else
            {
                account = await state.LockAccount(identity.AccountId, ct) ?? throw Failure("identity_unavailable", 503);
                now = clock.GetUtcNow();
                if (!UsableProof(proof, browser, now)) throw Failure("invalid_login_proof", 409);
                if (account.Status != AccountStatus.Active) throw Failure("account_unavailable", 403);
                if (account.AuthenticationRevokedAt is { } cutoff && (proof.CreatedAt <= cutoff || proof.ValidatedAt <= cutoff))
                    throw Failure("new_sign_in_required", 401);
                identity.Observe(proof, now);
            }
            proof.Consume(now);
            var session = await NewSession(state, account, identity, browser!, proof, now, ct);
            state.AddAudit(Audit(registered ? "account.registered" : "session.created", account.Id,
                browser!.Id, proof.Id, session.Version, correlationId, now));
            var grants = await state.Grants(account.Id, ct);
            return Identity(account, session, Capabilities(grants));
        }, cancellationToken);

    public Task<SessionIdentity> Resolve(string digest, Guid sessionId, CancellationToken cancellationToken) =>
        transactions.Run(async (state, ct) =>
        {
            var (_, account, session) = await ResolveLocked(state, digest, sessionId, ct);
            return Identity(account, session, Capabilities(await state.Grants(account.Id, ct)));
        }, cancellationToken);

    public Task<IReadOnlyList<SessionSummary>> List(string digest, CancellationToken cancellationToken) =>
        transactions.Run<IReadOnlyList<SessionSummary>>(async (state, ct) =>
        {
            var browser = await state.LockBrowser(digest, ct);
            var now = clock.GetUtcNow();
            if (browser is null || browser.RevokedAt is not null || browser.ExpiresAt <= now) return [];
            var result = new List<SessionSummary>();
            foreach (var session in (await state.BrowserSessions(browser.Id, ct)).OrderBy(s => s.AccountId))
            {
                if (!session.IsUsable(now)) continue;
                var account = await state.LockAccount(session.AccountId, ct);
                now = clock.GetUtcNow();
                if (!session.IsUsable(now) || browser.ExpiresAt <= now) continue;
                if (account?.Status == AccountStatus.Active &&
                    (account.AuthenticationRevokedAt is null || session.AuthenticatedAt > account.AuthenticationRevokedAt))
                    result.Add(new(account.Id, session.Id, account.DisplayName));
            }
            return result;
        }, cancellationToken);

    public Task<bool> LogoutCurrent(string digest, Guid sessionId, string correlationId, CancellationToken cancellationToken) =>
        transactions.Run(async (state, ct) =>
        {
            var browser = await state.LockBrowser(digest, ct) ?? throw Failure("invalid_session", 401);
            var session = (await state.BrowserSessions(browser.Id, ct)).SingleOrDefault(s => s.Id == sessionId)
                ?? throw Failure("invalid_session", 401);
            if (session.RevokedAt is null)
            {
                session.Revoke(clock.GetUtcNow());
                state.AddAudit(Audit("session.logged_out", session.AccountId, browser.Id, null, session.Version, correlationId, clock.GetUtcNow()));
            }
            return true;
        }, cancellationToken);

    public Task<bool> LogoutContainer(string digest, string correlationId, CancellationToken cancellationToken) =>
        transactions.Run(async (state, ct) =>
        {
            var browser = await state.LockBrowser(digest, ct);
            if (browser is null || browser.RevokedAt is not null) return true;
            var now = clock.GetUtcNow();
            foreach (var session in await state.BrowserSessions(browser.Id, ct)) session.Revoke(now);
            browser.Revoke(now);
            state.AddAudit(Audit("container.logged_out", null, browser.Id, null, browser.Version, correlationId, now));
            return true;
        }, cancellationToken);

    // Internal authority only; HTTP adapter must authenticate the caller before invoking this.
    public Task<AuthorityEvidence> AuthorizeOrganization(string digest, Guid sessionId, string action,
        Guid requestId, string correlationId, CancellationToken cancellationToken) =>
        transactions.Run(async (state, ct) =>
        {
            if (requestId == Guid.Empty || action is not "organization.account" and not "platform.companies.read" and not "platform.companies.review")
                throw Failure("invalid_authority_request", 400);
            var (browser, account, session) = await ResolveLocked(state, digest, sessionId, ct);
            var capabilities = Capabilities(await state.Grants(account.Id, ct));
            var now = clock.GetUtcNow();
            if (action != "organization.account" &&
                (!capabilities.Contains(action, StringComparer.Ordinal) || !assurance.IsAvailable ||
                 !Fresh(session.MfaObserved, session.ProviderAuthenticatedAt, session.EvidenceObservedAt, now)))
                throw Failure("authority_denied", 403);
            var evidenceId = Guid.CreateVersion7();
            state.AddAudit(new AuthenticationAudit(evidenceId, "authority.validated", account.Id, browser.Id, null, now,
                account.Version, correlationId));
            return new AuthorityEvidence(evidenceId, requestId, "organization", action, now,
                Identity(account, session, action == "organization.account" ? [] : new[] { action }));
        }, cancellationToken);

    public Task<Guid> CompanyOperation(string digest, Guid sessionId, string correlation, CancellationToken ct) =>
        transactions.Run(async (state, token) =>
        {
            var (browser, account, _) = await ResolveLocked(state, digest, sessionId, token);
            if (account.InitialRegistrationOperationId is { } initial) return initial;
            var grants = Capabilities(await state.Grants(account.Id, token));
            if (!grants.Contains("platform.owner", StringComparer.Ordinal)) throw Failure("operation_denied", 403);
            var existing = account.CompanyCreationOperationId;
            var id = account.EnsureCompanyCreationOperation(clock.GetUtcNow());
            if (existing is null) state.AddAudit(new AuthenticationAudit(Guid.CreateVersion7(), "company.operation_issued",
                account.Id, browser.Id, null, clock.GetUtcNow(), account.Version, correlation, account.Id));
            return id;
        }, ct);

    // Deliberately limited to the two reviewer capabilities. Owner/recovery/status powers stay closed.
    public Task<Guid> ReviewerGrant(string digest, Guid sessionId, Guid targetId, string capability,
        bool active, Guid? expectedVersion, string key, string correlation, CancellationToken ct) => transactions.Run(async (state, token) =>
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 100 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                throw Failure("invalid_idempotency_key", 422);
            await state.LockSetup(token);
            var (browser, actor, session) = await ResolveLocked(state, digest, sessionId, token);
            var grants = Capabilities(await state.Grants(actor.Id, token));
            if (capability is not "platform.companies.read" and not "platform.companies.review" || actor.Id == targetId ||
                !grants.Contains("platform.owner", StringComparer.Ordinal) || !grants.Contains("platform.grants.manage", StringComparer.Ordinal))
                throw Failure("grant_denied", 403);
            var target = await state.LockAccount(targetId, token);
            var targetGrants = await state.Grants(targetId, token);
            var now = clock.GetUtcNow();
            if (!assurance.IsAvailable || browser.ExpiresAt <= now || !session.IsUsable(now) ||
                !Fresh(session.MfaObserved, session.ProviderAuthenticatedAt, session.EvidenceObservedAt, now))
                throw Failure("authority_denied", 403);
            if (target?.Status != AccountStatus.Active) throw Failure("target_unavailable", 409);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new { targetId, capability, active, expectedVersion })));
            var receipt = await state.GrantReceipt(actor.Id, key, token);
            if (receipt is not null && receipt.ExpiresAt > now)
            {
                if (receipt.PayloadHash != hash) throw Failure("idempotency_payload_conflict", 409);
                return receipt.ResultVersion;
            }
            if (receipt is not null) { state.RemoveGrantReceipt(receipt); await state.Save(token); }
            var grant = targetGrants.SingleOrDefault(g => g.CapabilityKey == capability);
            if (grant?.Version != expectedVersion) throw Failure("grant_version_conflict", 412);
            if (grant is null)
            {
                if (!active) throw Failure("grant_not_found", 404);
                grant = new PlatformGrant(targetId, capability, actor.Id, now, null, null, Guid.CreateVersion7()); state.AddGrant(grant);
            }
            else grant.SetActive(active, actor.Id, now);
            state.AddAudit(new AuthenticationAudit(Guid.CreateVersion7(), active ? "reviewer.granted" : "reviewer.revoked",
                actor.Id, browser.Id, null, now, grant.Version, correlation, targetId, expectedVersion, capability));
            state.AddGrantReceipt(new GrantReceipt(actor.Id, key, hash, grant.Version, now.AddDays(7)));
            return grant.Version;
        }, ct);

    public Task<IReadOnlyList<ReviewerGrantView>> ReviewerGrants(string digest, Guid sessionId, Guid targetId, CancellationToken ct) =>
        transactions.Run<IReadOnlyList<ReviewerGrantView>>(async (state, token) =>
        {
            var (_, actor, session) = await ResolveLocked(state, digest, sessionId, token);
            var grants = Capabilities(await state.Grants(actor.Id, token));
            var targetGrants = await state.Grants(targetId, token);
            if (!grants.Contains("platform.owner", StringComparer.Ordinal) || !grants.Contains("platform.grants.manage", StringComparer.Ordinal) ||
                !assurance.IsAvailable || !Fresh(session.MfaObserved, session.ProviderAuthenticatedAt, session.EvidenceObservedAt, clock.GetUtcNow()))
                throw Failure("authority_denied", 403);
            return targetGrants.Where(g => g.CapabilityKey is "platform.companies.read" or "platform.companies.review")
                .Select(g => new ReviewerGrantView(g.CapabilityKey, g.RevokedAt is null, g.Version)).ToArray();
        }, ct);

    private async Task<(BrowserSession Browser, Account Account, AccountSession Session)> ResolveLocked(
        IAuthenticationState state, string digest, Guid sessionId, CancellationToken ct)
    {
        var browser = await state.LockBrowser(digest, ct);
        if (browser is null || browser.RevokedAt is not null) throw Failure("invalid_session", 401);
        // Browser lock serializes local session writes. Account lock precedes session updates.
        var candidate = (await state.BrowserSessions(browser.Id, ct)).SingleOrDefault(s => s.Id == sessionId)
            ?? throw Failure("invalid_session", 401);
        var account = await state.LockAccount(candidate.AccountId, ct) ?? throw Failure("invalid_session", 401);
        var session = await state.LockSession(sessionId, ct) ?? throw Failure("invalid_session", 401);
        var now = clock.GetUtcNow();
        if (account.Status != AccountStatus.Active) throw Failure("account_unavailable", 403);
        if (browser.ExpiresAt <= now || !session.IsUsable(now) || (account.AuthenticationRevokedAt is { } cutoff && session.AuthenticatedAt <= cutoff))
            throw Failure("invalid_session", 401);
        session.Touch(now);
        return (browser, account, session);
    }

    private static bool UsableProof(PendingLogin? proof, BrowserSession? browser, DateTimeOffset now) =>
        browser is not null && browser.RevokedAt is null && browser.ExpiresAt > now &&
        proof is not null && proof.BrowserSessionId == browser.Id && proof.ConsumedAt is null &&
        proof.ValidatedAt is { } validated && validated <= now && proof.ExpiresAt > now &&
        proof.Issuer == "https://accounts.google.com" && !string.IsNullOrWhiteSpace(proof.Subject);

    private static bool Fresh(bool mfa, DateTimeOffset? authenticated, DateTimeOffset? observed, DateTimeOffset now) =>
        mfa && authenticated is { } time && time >= now.AddMinutes(-5) && time <= now.AddSeconds(30) &&
        observed is { } seen && seen <= now && seen >= now.AddMinutes(-5);

    private static ExternalIdentity NewIdentity(Guid accountId, PendingLogin proof, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), accountId, "Google", proof.Issuer!, proof.Subject!, proof.Email,
            proof.EmailVerified, proof.HostedDomain, now, now, Guid.CreateVersion7());

    private static async Task<AccountSession> NewSession(IAuthenticationState state, Account account,
        ExternalIdentity identity, BrowserSession browser, PendingLogin proof, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var existing in (await state.BrowserSessions(browser.Id, ct)).Where(s => s.AccountId == account.Id))
            existing.Revoke(now);
        // Flush revocations before the partial unique key admits the replacement session.
        await state.Save(ct);
        var absolute = now.AddHours(12) < browser.ExpiresAt ? now.AddHours(12) : browser.ExpiresAt;
        var idle = now.AddMinutes(30) < absolute ? now.AddMinutes(30) : absolute;
        var session = new AccountSession(Guid.CreateVersion7(), browser.Id, account.Id, identity.Id,
            now, now, idle, absolute, null, Guid.CreateVersion7());
        session.SetEvidence(proof); state.AddSession(session);
        return session;
    }

    private static string[] Capabilities(IReadOnlyList<PlatformGrant> grants) => grants
        .Where(g => g.RevokedAt is null && PlatformCapabilities.Contains(g.CapabilityKey))
        .Select(g => g.CapabilityKey).ToArray();

    private static SessionIdentity Identity(Account account, AccountSession session, IReadOnlyList<string> capabilities) =>
        new(account.Id, session.Id, account.DisplayName, account.Version, account.InitialRegistrationOperationId,
            session.ExpiresAt, session.IdleExpiresAt, capabilities, account.CompanyCreationOperationId);

    private static AuthenticationAudit Audit(string name, Guid? actor, Guid browser, Guid? proof, Guid version, string correlation, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), name, actor, browser, proof, now, version, correlation);

    private static AuthenticationFailure Failure(string code, int status) => new(code, status);
}
