namespace Worktandem.Organization.Domain;

public sealed class ReviewDecision(Guid id, Guid companyId, Guid applicationId, Guid reviewerAccountId, Guid reviewedApplicationVersion, ReviewOutcome outcome, DateTimeOffset decidedAt, string platformAuthorizationEvidenceRef, string idempotencyKey, string? applicantVisibleReason, string? internalNote)
{
    public Guid Id { get; private set; } = id;
    public Guid CompanyId { get; private set; } = companyId;
    public Guid ApplicationId { get; private set; } = applicationId;
    public Guid ReviewerAccountId { get; private set; } = reviewerAccountId;
    public Guid ReviewedApplicationVersion { get; private set; } = reviewedApplicationVersion;
    public ReviewOutcome Outcome { get; private set; } = outcome;
    public DateTimeOffset DecidedAt { get; private set; } = decidedAt;
    public string PlatformAuthorizationEvidenceRef { get; private set; } = platformAuthorizationEvidenceRef;
    public string IdempotencyKey { get; private set; } = idempotencyKey;
    public string? ApplicantVisibleReason { get; private set; } = applicantVisibleReason;
    public string? InternalNote { get; private set; } = internalNote;
}
