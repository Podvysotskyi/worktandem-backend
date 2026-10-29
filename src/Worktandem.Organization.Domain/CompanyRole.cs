namespace Worktandem.Organization.Domain;

public sealed class CompanyRole(Guid id, Guid companyId, string name, string normalizedName, bool isSystem, DateTimeOffset createdAt, DateTimeOffset updatedAt, Guid version)
{
    public Guid Id { get; private set; } = id;
    public Guid CompanyId { get; private set; } = companyId;
    public string Name { get; private set; } = name;
    public string NormalizedName { get; private set; } = normalizedName;
    public bool IsSystem { get; private set; } = isSystem;
    public DateTimeOffset CreatedAt { get; private set; } = createdAt;
    public DateTimeOffset UpdatedAt { get; private set; } = updatedAt;
    public Guid Version { get; private set; } = version;
}
