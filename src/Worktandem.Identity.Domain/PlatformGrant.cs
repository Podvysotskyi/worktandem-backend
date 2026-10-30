namespace Worktandem.Identity.Domain;

public sealed class PlatformGrant(Guid accountId, string capabilityKey, Guid? grantedByAccountId, DateTimeOffset grantedAt, DateTimeOffset? revokedAt, Guid? revokedByAccountId, Guid version)
{
    public Guid AccountId { get; private set; } = accountId;
    public string CapabilityKey { get; private set; } = capabilityKey;
    public Guid? GrantedByAccountId { get; private set; } = grantedByAccountId;
    public DateTimeOffset GrantedAt { get; private set; } = grantedAt;
    public DateTimeOffset? RevokedAt { get; private set; } = revokedAt;
    public Guid? RevokedByAccountId { get; private set; } = revokedByAccountId;
    public Guid Version { get; private set; } = version;

    public void SetActive(bool active, Guid actor, DateTimeOffset now)
    {
        if (active) { GrantedByAccountId = actor; GrantedAt = now; RevokedAt = null; RevokedByAccountId = null; }
        else { RevokedAt = now; RevokedByAccountId = actor; }
        Version = Guid.CreateVersion7();
    }
}
