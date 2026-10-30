using Microsoft.EntityFrameworkCore;
using Worktandem.Identity.Application;
using Worktandem.Identity.Domain;

namespace Worktandem.Identity.Infrastructure;

public sealed class PostgresPendingLoginStore(IdentityDbContext database, TimeProvider clock) : IPendingLoginStore
{
    public async Task<Guid> Begin(string browserDigest, LoginPurpose purpose, string returnPath, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var setup = (await database.Set<SetupState>().FromSqlRaw("SELECT * FROM setup_state WHERE id='platform' FOR SHARE")
            .AsNoTracking().ToListAsync(cancellationToken)).SingleOrDefault()
            ?? throw new LoginFlowException("identity_unavailable");
        if (purpose == LoginPurpose.Bootstrap)
        {
            if (setup.Status != SetupStatus.Incomplete || await database.Set<Account>().AnyAsync(cancellationToken))
                throw new LoginFlowException("setup_unavailable");
        }
        else if (setup.Status != SetupStatus.Completed) throw new LoginFlowException("setup_required");

        // All calls for this browser take a database transaction lock, including across replicas.
        // The digest contains no raw browser credential; collisions only serialize unrelated browsers.
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({browserDigest}, 0))", cancellationToken);
        var browser = await database.Set<BrowserSession>().SingleOrDefaultAsync(b => b.CredentialDigest == browserDigest, cancellationToken);
        var now = clock.GetUtcNow();
        if (browser is null)
        {
            browser = new BrowserSession(Guid.CreateVersion7(), browserDigest, now, now.AddHours(12), null, Guid.CreateVersion7());
            database.Add(browser);
        }
        if (browser.RevokedAt is not null || browser.ExpiresAt <= now)
            throw new LoginFlowException("browser_session_expired");
        if (await database.Set<PendingLogin>().CountAsync(p => p.BrowserSessionId == browser.Id && p.ConsumedAt == null && p.ExpiresAt > now, cancellationToken) >= 5)
            throw new LoginFlowException("too_many_login_flows");
        var flow = new PendingLogin(Guid.CreateVersion7(), browser.Id, purpose, returnPath, now, now.AddMinutes(5), Guid.CreateVersion7());
        database.Add(flow);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return flow.Id;
    }

    public async Task<string> Validate(Guid flowId, string browserDigest, GoogleLoginEvidence evidence, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var flow = (await database.Set<PendingLogin>().FromSqlInterpolated(
            $"SELECT * FROM pending_logins WHERE id={flowId} FOR UPDATE").ToListAsync(cancellationToken)).SingleOrDefault();
        var browser = flow is null ? null : await database.Set<BrowserSession>().AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == flow.BrowserSessionId && b.CredentialDigest == browserDigest, cancellationToken);
        var now = clock.GetUtcNow();
        if (flow is null || flow.ValidatedAt is not null || flow.ExpiresAt <= now ||
            browser is null || browser.RevokedAt is not null || browser.ExpiresAt <= now)
            throw new LoginFlowException("invalid_login_flow");
        flow.RecordValidatedProof(evidence, now);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return flow.ReturnPath;
    }

    public async Task<PendingLoginSummary?> Read(Guid flowId, string browserDigest, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return await (from flow in database.Set<PendingLogin>().AsNoTracking()
                      join browser in database.Set<BrowserSession>() on flow.BrowserSessionId equals browser.Id
                      where flow.Id == flowId && flow.ConsumedAt == null && flow.ExpiresAt > now && browser.CredentialDigest == browserDigest &&
                            browser.RevokedAt == null && browser.ExpiresAt > now
                      select new PendingLoginSummary(flow.Id, flow.Purpose, flow.ExpiresAt, flow.ValidatedAt != null))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
