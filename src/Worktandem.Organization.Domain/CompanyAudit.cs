namespace Worktandem.Organization.Domain;

public sealed class CompanyAudit(Guid id, Guid companyId, Guid applicationId, Guid actorAccountId,
    string eventName, Guid? priorVersion, Guid resultVersion, Guid authorityEvidenceId,
    DateTimeOffset occurredAt, string correlationId)
{
    public Guid Id { get; private set; } = id;
    public Guid CompanyId { get; private set; } = companyId;
    public Guid ApplicationId { get; private set; } = applicationId;
    public Guid ActorAccountId { get; private set; } = actorAccountId;
    public string EventName { get; private set; } = eventName;
    public Guid? PriorVersion { get; private set; } = priorVersion;
    public Guid ResultVersion { get; private set; } = resultVersion;
    public Guid AuthorityEvidenceId { get; private set; } = authorityEvidenceId;
    public DateTimeOffset OccurredAt { get; private set; } = occurredAt;
    public string CorrelationId { get; private set; } = correlationId;
}
