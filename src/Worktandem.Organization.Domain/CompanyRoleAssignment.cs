namespace Worktandem.Organization.Domain;

public sealed class CompanyRoleAssignment(Guid companyId, Guid membershipId, Guid roleId, DateTimeOffset assignedAt)
{
    public Guid CompanyId { get; private set; } = companyId;
    public Guid MembershipId { get; private set; } = membershipId;
    public Guid RoleId { get; private set; } = roleId;
    public DateTimeOffset AssignedAt { get; private set; } = assignedAt;
}
