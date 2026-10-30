namespace Worktandem.Identity.Domain;

public sealed class Account(Guid id, string displayName, AccountStatus status, Guid? initialRegistrationOperationId, DateTimeOffset createdAt, DateTimeOffset updatedAt, Guid version)
{
    public Guid Id { get; private set; } = id;
    public string DisplayName { get; private set; } = displayName;
    public AccountStatus Status { get; private set; } = status;
    public Guid? InitialRegistrationOperationId { get; private set; } = initialRegistrationOperationId;
    public DateTimeOffset CreatedAt { get; private set; } = createdAt;
    public DateTimeOffset UpdatedAt { get; private set; } = updatedAt;
    public Guid Version { get; private set; } = version;
    public DateTimeOffset? AuthenticationRevokedAt { get; private set; }

    // Trusted status workflows must call this and revoke sessions in their owning transaction.
    public void InvalidateAuthentication(DateTimeOffset now)
    {
        AuthenticationRevokedAt = now;
        UpdatedAt = now;
        Version = Guid.CreateVersion7();
    }

    public void Rename(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (displayName.Length > 200) throw new ArgumentOutOfRangeException(nameof(displayName));
        DisplayName = displayName.Trim();
        UpdatedAt = DateTimeOffset.UtcNow;
        Version = Guid.CreateVersion7();
    }
}
