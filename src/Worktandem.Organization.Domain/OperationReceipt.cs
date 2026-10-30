namespace Worktandem.Organization.Domain;

public sealed class OperationReceipt(Guid actorAccountId, string operation, Guid targetId, string key,
    string payloadHash, string resultJson, DateTimeOffset expiresAt)
{
    public Guid ActorAccountId { get; private set; } = actorAccountId;
    public string Operation { get; private set; } = operation;
    public Guid TargetId { get; private set; } = targetId;
    public string Key { get; private set; } = key;
    public string PayloadHash { get; private set; } = payloadHash;
    public string ResultJson { get; private set; } = resultJson;
    public DateTimeOffset ExpiresAt { get; private set; } = expiresAt;
}
