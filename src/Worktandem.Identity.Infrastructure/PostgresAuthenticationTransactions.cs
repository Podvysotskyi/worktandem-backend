using Microsoft.EntityFrameworkCore;
using Npgsql;
using Worktandem.Identity.Application;
using Worktandem.Identity.Domain;

namespace Worktandem.Identity.Infrastructure;

public sealed class PostgresAuthenticationTransactions(IdentityDbContext database) : IAuthenticationTransactions
{
    public async Task<T> Run<T>(Func<IAuthenticationState, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            database.ChangeTracker.Clear();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await work(new State(database), cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (Exception failure) when (attempt < 2 && Retryable(failure))
            {
                await transaction.RollbackAsync(cancellationToken);
                database.ChangeTracker.Clear();
                await Task.Delay(25 * (attempt + 1), cancellationToken);
            }
        }
    }

    private static bool Retryable(Exception failure) => failure is DbUpdateConcurrencyException ||
        (failure is PostgresException postgres && postgres.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected) ||
        (failure is DbUpdateException update && update.InnerException is PostgresException inner &&
            inner.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected);

    private sealed class State(IdentityDbContext db) : IAuthenticationState
    {
        public async Task<SetupState?> LockSetup(CancellationToken ct) =>
            (await db.Set<SetupState>().FromSqlRaw("SELECT * FROM setup_state WHERE id='platform' FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        public async Task<PendingLogin?> LockProof(Guid id, CancellationToken ct) =>
            (await db.Set<PendingLogin>().FromSqlInterpolated($"SELECT * FROM pending_logins WHERE id={id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        public async Task<BrowserSession?> LockBrowser(string digest, CancellationToken ct) =>
            (await db.Set<BrowserSession>().FromSqlInterpolated($"SELECT * FROM browser_sessions WHERE credential_digest={digest} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        public async Task<Account?> LockAccount(Guid id, CancellationToken ct) =>
            (await db.Set<Account>().FromSqlInterpolated($"SELECT * FROM accounts WHERE id={id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        public async Task<AccountSession?> LockSession(Guid id, CancellationToken ct)
        {
            // A prior chooser read is never the authoritative locked session state.
            foreach (var entry in db.ChangeTracker.Entries<AccountSession>().Where(e => e.Entity.Id == id).ToArray())
                entry.State = EntityState.Detached;
            return (await db.Set<AccountSession>().FromSqlInterpolated($"SELECT * FROM account_sessions WHERE id={id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        }
        public Task<ExternalIdentity?> FindGoogle(string issuer, string subject, CancellationToken ct) =>
            db.Set<ExternalIdentity>().SingleOrDefaultAsync(i => i.Provider == "Google" && i.Issuer == issuer && i.Subject == subject, ct);
        public Task<bool> HasAccounts(CancellationToken ct) => db.Set<Account>().AnyAsync(ct);
        public async Task<IReadOnlyList<AccountSession>> BrowserSessions(Guid id, CancellationToken ct) =>
            await db.Set<AccountSession>().Where(s => s.BrowserSessionId == id).ToListAsync(ct);
        public async Task<IReadOnlyList<PlatformGrant>> Grants(Guid id, CancellationToken ct) =>
            await db.Set<PlatformGrant>().Where(g => g.AccountId == id).ToListAsync(ct);
        public async Task MarkAttemptSucceeded(Guid id, CancellationToken ct) =>
            (await db.Set<SetupAttempt>().SingleAsync(a => a.Id == id, ct)).MarkSucceeded();
        public void AddAccount(Account account) => db.Add(account);
        public void AddIdentity(ExternalIdentity identity) => db.Add(identity);
        public void AddGrant(PlatformGrant grant) => db.Add(grant);
        public void AddSession(AccountSession session) => db.Add(session);
        public void AddAudit(AuthenticationAudit audit) => db.Add(audit);
        public Task Save(CancellationToken ct) => db.SaveChangesAsync(ct);
    }
}
