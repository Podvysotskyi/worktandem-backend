namespace Worktandem.Identity.Domain;

public enum LoginPurpose { Bootstrap, Login, Register, AddAccount }

public sealed record GoogleLoginEvidence(string Issuer, string Subject, string? Email,
    bool EmailVerified, string? HostedDomain, DateTimeOffset? ProviderAuthenticatedAt, bool MfaObserved);
