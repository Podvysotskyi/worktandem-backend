namespace Worktandem.Organization.Domain;

public sealed class RolePermission(Guid companyId, Guid roleId, string permissionKey)
{
    public Guid CompanyId { get; private set; } = companyId;
    public Guid RoleId { get; private set; } = roleId;
    public string PermissionKey { get; private set; } = permissionKey;
}
