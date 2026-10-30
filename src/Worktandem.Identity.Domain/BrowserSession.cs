namespace Worktandem.Identity.Domain;

public sealed class BrowserSession(Guid id, string credentialDigest, DateTimeOffset createdAt, DateTimeOffset expiresAt, DateTimeOffset? revokedAt, Guid version)
{
    public Guid Id { get; private set; } = id;
    public string CredentialDigest { get; private set; } = credentialDigest;
    public DateTimeOffset CreatedAt { get; private set; } = createdAt;
    public DateTimeOffset ExpiresAt { get; private set; } = expiresAt;
    public DateTimeOffset? RevokedAt { get; private set; } = revokedAt;
    public Guid Version { get; private set; } = version;

    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null) return;
        RevokedAt = now;
        Version = Guid.CreateVersion7();
    }
}
