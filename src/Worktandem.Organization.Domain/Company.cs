namespace Worktandem.Organization.Domain;

public sealed class Company(Guid id, string name, string slug, CompanyStatus status, Guid? currentApplicationId, Guid createdByAccountId, Guid creationOperationId, DateTimeOffset createdAt, DateTimeOffset updatedAt, Guid version)
{
    public Guid Id { get; private set; } = id;
    public string Name { get; private set; } = name;
    public string Slug { get; private set; } = slug;
    public CompanyStatus Status { get; private set; } = status;
    public Guid? CurrentApplicationId { get; private set; } = currentApplicationId;
    public Guid CreatedByAccountId { get; private set; } = createdByAccountId;
    public Guid CreationOperationId { get; private set; } = creationOperationId;
    public DateTimeOffset CreatedAt { get; private set; } = createdAt;
    public DateTimeOffset UpdatedAt { get; private set; } = updatedAt;
    public Guid Version { get; private set; } = version;

    public void Submit(CompanyApplication application, DateTimeOffset now)
    {
        if (application.CompanyId != Id || application.ApplicantAccountId != CreatedByAccountId ||
            (CurrentApplicationId is not null && Status != CompanyStatus.Rejected))
            throw new InvalidOperationException("Application cannot be submitted.");
        CurrentApplicationId = application.Id;
        Name = application.CompanyName;
        Status = CompanyStatus.Pending;
        UpdatedAt = now;
        Version = Guid.CreateVersion7();
    }

    public void Decide(ReviewOutcome outcome, DateTimeOffset now)
    {
        if (Status != CompanyStatus.Pending || CurrentApplicationId is null)
            throw new InvalidOperationException("Company is not pending.");
        Status = outcome == ReviewOutcome.Approved ? CompanyStatus.Approved : CompanyStatus.Rejected;
        UpdatedAt = now;
        Version = Guid.CreateVersion7();
    }
}
