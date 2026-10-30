namespace Worktandem.Identity.Domain;

public sealed class AuthenticationAudit(Guid id, string eventName, Guid? actorAccountId,
    Guid browserSessionId, Guid? pendingLoginId, DateTimeOffset occurredAt, Guid resultVersion, string correlationId)
{
    public Guid Id { get; private set; } = id;
    public string EventName { get; private set; } = eventName;
    public Guid? ActorAccountId { get; private set; } = actorAccountId;
    public Guid BrowserSessionId { get; private set; } = browserSessionId;
    // Temporary proof reference deliberately has no FK, so its expiry cleanup cannot erase audit.
    public Guid? PendingLoginId { get; private set; } = pendingLoginId;
    public DateTimeOffset OccurredAt { get; private set; } = occurredAt;
    public Guid ResultVersion { get; private set; } = resultVersion;
    public string CorrelationId { get; private set; } = correlationId;
}
