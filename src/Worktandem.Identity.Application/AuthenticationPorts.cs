using Worktandem.Identity.Domain;

namespace Worktandem.Identity.Application;

public sealed class AuthenticationFailure(string code, int status) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public interface IBootstrapSecretVerifier
{
    bool IsConfigured { get; }
    bool Matches(string entered);
}

public interface IPrivilegedAssuranceGate
{
    bool IsAvailable { get; }
}

public interface ISetupAttemptBudget
{
    Task<Guid> Reserve(Guid pendingLoginId, string sourceHash, CancellationToken cancellationToken);
}

// Explicit transaction boundary for the actual Identity workflows, never a public repository or HTTP contract.
public interface IAuthenticationTransactions
{
    Task<T> Run<T>(Func<IAuthenticationState, CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

public interface IAuthenticationState
{
    Task<SetupState?> LockSetup(CancellationToken cancellationToken);
    Task<PendingLogin?> LockProof(Guid id, CancellationToken cancellationToken);
    Task<BrowserSession?> LockBrowser(string digest, CancellationToken cancellationToken);
    Task<Account?> LockAccount(Guid id, CancellationToken cancellationToken);
    Task<AccountSession?> LockSession(Guid id, CancellationToken cancellationToken);
    Task<ExternalIdentity?> FindGoogle(string issuer, string subject, CancellationToken cancellationToken);
    Task<bool> HasAccounts(CancellationToken cancellationToken);
    Task<IReadOnlyList<AccountSession>> BrowserSessions(Guid browserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlatformGrant>> Grants(Guid accountId, CancellationToken cancellationToken);
    Task<GrantReceipt?> GrantReceipt(Guid actor, string key, CancellationToken cancellationToken);
    void AddGrantReceipt(GrantReceipt receipt);
    void RemoveGrantReceipt(GrantReceipt receipt);
    Task MarkAttemptSucceeded(Guid id, CancellationToken cancellationToken);
    void AddAccount(Account account);
    void AddIdentity(ExternalIdentity identity);
    void AddGrant(PlatformGrant grant);
    void AddSession(AccountSession session);
    void AddAudit(AuthenticationAudit audit);
    Task Save(CancellationToken cancellationToken);
}

public sealed record SessionIdentity(Guid AccountId, Guid AccountSessionId, string DisplayName,
    Guid AccountVersion, Guid? InitialRegistrationOperationId, DateTimeOffset ExpiresAt,
    DateTimeOffset IdleExpiresAt, IReadOnlyList<string> Capabilities, Guid? CompanyCreationOperationId = null);

public sealed record SessionSummary(Guid AccountId, Guid AccountSessionId, string DisplayName);
public sealed record ReviewerGrantView(string Capability, bool Active, Guid Version);

public sealed record AuthorityEvidence(Guid EvidenceId, Guid RequestId, string Service, string Action,
    DateTimeOffset ValidatedAt, SessionIdentity Identity);
