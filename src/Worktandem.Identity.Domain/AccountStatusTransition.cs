namespace Worktandem.Identity.Domain;

public sealed class AccountStatusTransition(Guid id, Guid accountId, Guid actorAccountId, AccountStatus previousStatus, AccountStatus newStatus, string? reason, DateTimeOffset occurredAt, Guid expectedAccountVersion, Guid resultAccountVersion, string operationId)
{
    public Guid Id { get; private set; } = id;
    public Guid AccountId { get; private set; } = accountId;
    public Guid ActorAccountId { get; private set; } = actorAccountId;
    public AccountStatus PreviousStatus { get; private set; } = previousStatus;
    public AccountStatus NewStatus { get; private set; } = newStatus;
    public string? Reason { get; private set; } = reason;
    public DateTimeOffset OccurredAt { get; private set; } = occurredAt;
    public Guid ExpectedAccountVersion { get; private set; } = expectedAccountVersion;
    public Guid ResultAccountVersion { get; private set; } = resultAccountVersion;
    public string OperationId { get; private set; } = operationId;
}
