using Worktandem.Organization.Domain;

namespace Worktandem.Organization.Application;

public sealed class CompanyFailure(string code, int status) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed record ActorRequest(string BrowserCredential, Guid AccountSessionId);
public sealed record OrganizationAuthority(Guid EvidenceId, Guid RequestId, string Service, string Action,
    DateTimeOffset ValidatedAt, AuthorityAccount Identity);
public sealed record AuthorityAccount(Guid AccountId, Guid AccountSessionId, string DisplayName,
    Guid AccountVersion, Guid? InitialRegistrationOperationId, DateTimeOffset ExpiresAt,
    DateTimeOffset IdleExpiresAt, IReadOnlyList<string> Capabilities, Guid? CompanyCreationOperationId = null);

public interface IIdentityAuthority
{
    Task<OrganizationAuthority> Validate(ActorRequest actor, string action, CancellationToken ct);
}

public sealed record CompanyForm(string CompanyName, string CountryCode, string BusinessDescription,
    string ApplicantRoleDescription, string? Website = null, string? LegalRegistrationId = null);
public sealed record ApplicationView(Guid CompanyId, Guid CompanyVersion, string Status, Guid ApplicationId,
    Guid ApplicationVersion, int SubmissionNumber, CompanyForm Form, string? ApplicantVisibleReason,
    DateTimeOffset SubmittedAt);
public sealed record ReviewView(ApplicationView Application, Guid ApplicantAccountId, string? InternalNote);
public sealed record WorkspaceView(Guid CompanyId, Guid CompanyVersion, string Name, Guid MembershipId,
    Guid AccountId, Guid AccountSessionId, IReadOnlyList<string> Permissions);
public sealed record CompanyContext(IReadOnlyList<ApplicationView> Applications, IReadOnlyList<WorkspaceView> Workspaces);
public sealed record ReviewCommand(ReviewOutcome Outcome, Guid ApplicationVersion, string? ApplicantVisibleReason,
    string? InternalNote);

public interface ICompanyTransactions
{
    Task<T> Run<T>(Func<CancellationToken, Task<OrganizationAuthority>> authorize,
        Func<ICompanyState, OrganizationAuthority, CancellationToken, Task<T>> work, CancellationToken ct);
}

public interface ICompanyState
{
    Task LockOperation(Guid actor, CancellationToken ct);
    Task<Company?> LockCompany(Guid id, CancellationToken ct);
    Task<Company?> ByOperation(Guid actor, Guid operation, CancellationToken ct);
    Task<CompanyApplication?> Application(Guid id, CancellationToken ct);
    Task<CompanyApplication?> FirstApplication(Guid company, CancellationToken ct);
    Task<ReviewDecision?> Decision(Guid application, CancellationToken ct);
    Task<IReadOnlyList<Company>> OwnCompanies(Guid actor, CancellationToken ct);
    Task<IReadOnlyList<Company>> ReviewQueue(int offset, int limit, CancellationToken ct);
    Task<IReadOnlyList<CompanyMembership>> Memberships(Guid actor, CancellationToken ct);
    Task<IReadOnlyList<string>> Permissions(Guid company, Guid member, CancellationToken ct);
    Task<OperationReceipt?> Receipt(Guid actor, string operation, Guid target, string key, CancellationToken ct);
    void RemoveReceipt(OperationReceipt receipt);
    void Add<T>(T entity) where T : class;
    Task Save(CancellationToken ct);
}
