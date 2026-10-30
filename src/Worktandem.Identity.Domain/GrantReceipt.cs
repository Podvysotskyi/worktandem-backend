namespace Worktandem.Identity.Domain;

public sealed class GrantReceipt(Guid actorAccountId, string key, string payloadHash, Guid resultVersion, DateTimeOffset expiresAt)
{
    public Guid ActorAccountId { get; private set; } = actorAccountId;
    public string Key { get; private set; } = key;
    public string PayloadHash { get; private set; } = payloadHash;
    public Guid ResultVersion { get; private set; } = resultVersion;
    public DateTimeOffset ExpiresAt { get; private set; } = expiresAt;
}
