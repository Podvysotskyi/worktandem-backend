namespace Worktandem.Identity.Domain;

public sealed class SetupAttempt(Guid id, Guid pendingLoginId, string sourceHash, DateTimeOffset occurredAt, Guid version)
{
    public Guid Id { get; private set; } = id;
    public Guid PendingLoginId { get; private set; } = pendingLoginId;
    public string SourceHash { get; private set; } = sourceHash;
    public DateTimeOffset OccurredAt { get; private set; } = occurredAt;
    public bool IsFailure { get; private set; } = true;
    public Guid Version { get; private set; } = version;
    public void MarkSucceeded() { IsFailure = false; Version = Guid.CreateVersion7(); }
}
