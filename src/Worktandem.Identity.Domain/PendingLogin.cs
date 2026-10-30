namespace Worktandem.Identity.Domain;

// Temporary proof only. Neither this ID nor a validated record is an account session.
public sealed class PendingLogin(Guid id, Guid browserSessionId, LoginPurpose purpose,
    string returnPath, DateTimeOffset createdAt, DateTimeOffset expiresAt, Guid version)
{
    public Guid Id { get; private set; } = id;
    public Guid BrowserSessionId { get; private set; } = browserSessionId;
    public LoginPurpose Purpose { get; private set; } = purpose;
    public string ReturnPath { get; private set; } = returnPath;
    public DateTimeOffset CreatedAt { get; private set; } = createdAt;
    public DateTimeOffset ExpiresAt { get; private set; } = expiresAt;
    public DateTimeOffset? ValidatedAt { get; private set; }
    public string? Issuer { get; private set; }
    public string? Subject { get; private set; }
    public string? Email { get; private set; }
    public bool EmailVerified { get; private set; }
    public string? HostedDomain { get; private set; }
    public DateTimeOffset? ProviderAuthenticatedAt { get; private set; }
    public bool MfaObserved { get; private set; }
    public Guid Version { get; private set; } = version;

    public void RecordValidatedProof(GoogleLoginEvidence evidence, DateTimeOffset now)
    {
        if (ValidatedAt is not null || ExpiresAt <= now)
            throw new InvalidOperationException("Login flow is unavailable.");
        if (evidence.Issuer != "https://accounts.google.com" ||
            string.IsNullOrWhiteSpace(evidence.Subject) || evidence.Subject.Length > 255 ||
            evidence.Email?.Length > 320 || evidence.HostedDomain?.Length > 253 ||
            evidence.ProviderAuthenticatedAt > now.AddSeconds(30))
            throw new ArgumentException("Invalid provider evidence.", nameof(evidence));
        Issuer = evidence.Issuer;
        Subject = evidence.Subject;
        Email = evidence.Email;
        EmailVerified = evidence.EmailVerified;
        HostedDomain = evidence.HostedDomain;
        ProviderAuthenticatedAt = evidence.ProviderAuthenticatedAt;
        MfaObserved = evidence.MfaObserved;
        ValidatedAt = now;
        ExpiresAt = now.AddMinutes(5);
        Version = Guid.CreateVersion7();
    }
}
