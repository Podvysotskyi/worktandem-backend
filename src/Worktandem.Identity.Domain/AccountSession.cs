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
}
