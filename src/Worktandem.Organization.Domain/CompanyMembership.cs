namespace Worktandem.Organization.Domain;

public sealed class CompanyMembership(Guid id, Guid companyId, Guid accountId, MembershipStatus status, DateTimeOffset joinedAt, DateTimeOffset? endedAt, DateTimeOffset createdAt, DateTimeOffset updatedAt, Guid version)
{
    public Guid Id { get; private set; } = id;
    public Guid CompanyId { get; private set; } = companyId;
    public Guid AccountId { get; private set; } = accountId;
    public MembershipStatus Status { get; private set; } = status;
    public DateTimeOffset JoinedAt { get; private set; } = joinedAt;
    public DateTimeOffset? EndedAt { get; private set; } = endedAt;
    public DateTimeOffset CreatedAt { get; private set; } = createdAt;
    public DateTimeOffset UpdatedAt { get; private set; } = updatedAt;
    public Guid Version { get; private set; } = version;
}
