namespace Worktandem.Identity.Domain;

public sealed class SetupState(string id, SetupStatus status, DateTimeOffset? completedAt, Guid? initialOwnerAccountId, Guid version)
{
    public string Id { get; private set; } = id;
    public SetupStatus Status { get; private set; } = status;
    public DateTimeOffset? CompletedAt { get; private set; } = completedAt;
    public Guid? InitialOwnerAccountId { get; private set; } = initialOwnerAccountId;
    public Guid Version { get; private set; } = version;

    public void Complete(Guid ownerAccountId, DateTimeOffset completedAt)
    {
        if (Status != SetupStatus.Incomplete) throw new InvalidOperationException("Setup is already complete.");
        Status = SetupStatus.Completed;
        InitialOwnerAccountId = ownerAccountId;
        CompletedAt = completedAt;
        Version = Guid.CreateVersion7();
    }
}
