using Worktandem.Identity.Domain;

namespace Worktandem.Identity.Application;

public sealed class LoginFlowException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record PendingLoginSummary(Guid Id, LoginPurpose Purpose, DateTimeOffset ExpiresAt, bool ProofValidated);

public interface IPendingLoginStore
{
    Task<Guid> Begin(string browserDigest, LoginPurpose purpose, string returnPath, CancellationToken cancellationToken);
    Task<string> Validate(Guid flowId, string browserDigest, GoogleLoginEvidence evidence, CancellationToken cancellationToken);
    Task<PendingLoginSummary?> Read(Guid flowId, string browserDigest, CancellationToken cancellationToken);
}

// Application owns the use-case boundary; HTTP, OIDC and EF remain adapters.
public sealed class PendingLoginWorkflow(IPendingLoginStore store)
{
    public Task<Guid> Begin(string browserDigest, LoginPurpose purpose, string returnPath, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(purpose)) throw new LoginFlowException("invalid_login_request");
        if (!SafeReturnPath(returnPath)) throw new LoginFlowException("invalid_return_path");
        return store.Begin(browserDigest, purpose, returnPath, cancellationToken);
    }

    public Task<string> Validate(Guid flowId, string browserDigest, GoogleLoginEvidence evidence, CancellationToken cancellationToken) =>
        store.Validate(flowId, browserDigest, evidence, cancellationToken);

    public Task<PendingLoginSummary?> Read(Guid flowId, string browserDigest, CancellationToken cancellationToken) =>
        store.Read(flowId, browserDigest, cancellationToken);

    // Fixed destinations, never a client URL, hostname, arbitrary scheme or encoded redirect.
    public static bool SafeReturnPath(string path) => path is "/" or "/setup" or "/auth/continue";
}
