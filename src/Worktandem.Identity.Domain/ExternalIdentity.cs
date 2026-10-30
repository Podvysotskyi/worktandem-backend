namespace Worktandem.Identity.Domain;

public sealed class ExternalIdentity(Guid id, Guid accountId, string provider, string issuer, string subject, string? email, bool emailVerified, string? hostedDomain, DateTimeOffset observedAt, DateTimeOffset associatedAt, Guid version)
{
    public Guid Id { get; private set; } = id;
    public Guid AccountId { get; private set; } = accountId;
    public string Provider { get; private set; } = provider;
    public string Issuer { get; private set; } = issuer;
    public string Subject { get; private set; } = subject;
    public string? Email { get; private set; } = email;
    public bool EmailVerified { get; private set; } = emailVerified;
    public string? HostedDomain { get; private set; } = hostedDomain;
    public DateTimeOffset ObservedAt { get; private set; } = observedAt;
    public DateTimeOffset AssociatedAt { get; private set; } = associatedAt;
    public Guid Version { get; private set; } = version;

    public void Observe(PendingLogin proof, DateTimeOffset now)
    {
        if (proof.Issuer != Issuer || proof.Subject != Subject)
            throw new InvalidOperationException("Identity ownership cannot change.");
        Email = proof.Email;
        EmailVerified = proof.EmailVerified;
        HostedDomain = proof.HostedDomain;
        ObservedAt = now;
        Version = Guid.CreateVersion7();
    }
}
