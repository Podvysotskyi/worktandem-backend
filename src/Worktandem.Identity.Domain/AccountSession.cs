namespace Worktandem.Identity.Domain;

public sealed class AccountSession(Guid id, Guid browserSessionId, Guid accountId, Guid externalIdentityId, DateTimeOffset authenticatedAt, DateTimeOffset lastSeenAt, DateTimeOffset idleExpiresAt, DateTimeOffset expiresAt, DateTimeOffset? revokedAt, Guid version)
{
    public Guid Id { get; private set; } = id;
    public Guid BrowserSessionId { get; private set; } = browserSessionId;
    public Guid AccountId { get; private set; } = accountId;
    public Guid ExternalIdentityId { get; private set; } = externalIdentityId;
    public DateTimeOffset AuthenticatedAt { get; private set; } = authenticatedAt;
    public DateTimeOffset LastSeenAt { get; private set; } = lastSeenAt;
    public DateTimeOffset IdleExpiresAt { get; private set; } = idleExpiresAt;
    public DateTimeOffset ExpiresAt { get; private set; } = expiresAt;
    public DateTimeOffset? RevokedAt { get; private set; } = revokedAt;
    public Guid Version { get; private set; } = version;
    public DateTimeOffset? ProviderAuthenticatedAt { get; private set; }
    public bool MfaObserved { get; private set; }
    public DateTimeOffset? EvidenceObservedAt { get; private set; }

    public void SetEvidence(PendingLogin proof)
    {
        ProviderAuthenticatedAt = proof.ProviderAuthenticatedAt;
        MfaObserved = proof.MfaObserved;
        EvidenceObservedAt = proof.ValidatedAt;
    }

    public bool IsUsable(DateTimeOffset now) =>
        RevokedAt is null && IdleExpiresAt > now && ExpiresAt > now;

    public void Touch(DateTimeOffset now)
    {
        if (!IsUsable(now)) throw new InvalidOperationException("Session is unavailable.");
        if (now < LastSeenAt) return;
        LastSeenAt = now;
        IdleExpiresAt = now.AddMinutes(30) < ExpiresAt ? now.AddMinutes(30) : ExpiresAt;
        Version = Guid.CreateVersion7();
    }

    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null) return;
        RevokedAt = now;
        Version = Guid.CreateVersion7();
    }
}
