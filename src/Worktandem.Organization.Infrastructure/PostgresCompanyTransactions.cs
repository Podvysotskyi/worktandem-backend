using Microsoft.EntityFrameworkCore;
using Npgsql;
using Worktandem.Organization.Application;
using Worktandem.Organization.Domain;

namespace Worktandem.Organization.Infrastructure;

public sealed class PostgresCompanyTransactions(OrganizationDbContext database) : ICompanyTransactions
{
    public async Task<T> Run<T>(Func<CancellationToken, Task<OrganizationAuthority>> authorize,
        Func<ICompanyState, OrganizationAuthority, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            database.ChangeTracker.Clear();
            // The live remote authority call precedes each local transaction, including known retries.
            var evidence = await authorize(ct);
            await using var transaction = await database.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await work(new State(database), evidence, ct);
                await database.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (Exception failure) when (attempt < 2 && Retryable(failure))
            {
                await transaction.RollbackAsync(ct);
                await Task.Delay(25 * (attempt + 1), ct);
            }
        }
    }
    private static bool Retryable(Exception failure) => failure is DbUpdateConcurrencyException ||
        (failure is PostgresException p && p.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected) ||
        (failure is DbUpdateException { InnerException: PostgresException inner } &&
            inner.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected);

    private sealed class State(OrganizationDbContext db) : ICompanyState
    {
        public async Task LockOperation(Guid actor, CancellationToken ct) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({actor.ToString("D")}, 17482))", ct);
        public async Task<Company?> LockCompany(Guid id, CancellationToken ct)
        {
            foreach (var entry in db.ChangeTracker.Entries<Company>().Where(e => e.Entity.Id == id).ToArray())
                entry.State = EntityState.Detached;
            return (await db.Set<Company>().FromSqlInterpolated($"SELECT * FROM companies WHERE id={id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        }
        public Task<Company?> ByOperation(Guid actor, Guid operation, CancellationToken ct) => db.Set<Company>().AsNoTracking()
            .SingleOrDefaultAsync(c => c.CreatedByAccountId == actor && c.CreationOperationId == operation, ct);
        public Task<CompanyApplication?> Application(Guid id, CancellationToken ct) =>
            db.Set<CompanyApplication>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        public Task<CompanyApplication?> FirstApplication(Guid company, CancellationToken ct) =>
            db.Set<CompanyApplication>().AsNoTracking().SingleOrDefaultAsync(a => a.CompanyId == company && a.SubmissionNumber == 1, ct);
        public Task<ReviewDecision?> Decision(Guid application, CancellationToken ct) =>
            db.Set<ReviewDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.ApplicationId == application, ct);
        public async Task<IReadOnlyList<Company>> OwnCompanies(Guid actor, CancellationToken ct) =>
            await db.Set<Company>().AsNoTracking().Where(c => c.CreatedByAccountId == actor).ToListAsync(ct);
        public async Task<IReadOnlyList<Company>> ReviewQueue(int offset, int limit, CancellationToken ct) =>
            await db.Set<Company>().AsNoTracking().Where(c => c.Status == CompanyStatus.Pending)
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Skip(offset).Take(limit).ToListAsync(ct);
        public async Task<IReadOnlyList<CompanyMembership>> Memberships(Guid actor, CancellationToken ct) =>
            await db.Set<CompanyMembership>().AsNoTracking().Where(m => m.AccountId == actor).ToListAsync(ct);
        public async Task<IReadOnlyList<string>> Permissions(Guid company, Guid member, CancellationToken ct) =>
            await (from assignment in db.Set<CompanyRoleAssignment>().AsNoTracking()
                   join permission in db.Set<RolePermission>().AsNoTracking()
                       on new { assignment.CompanyId, assignment.RoleId } equals new { permission.CompanyId, permission.RoleId }
                   where assignment.CompanyId == company && assignment.MembershipId == member
                   select permission.PermissionKey).Distinct().OrderBy(p => p).ToListAsync(ct);
        public Task<OperationReceipt?> Receipt(Guid actor, string operation, Guid target, string key, CancellationToken ct) =>
            db.Set<OperationReceipt>().SingleOrDefaultAsync(r => r.ActorAccountId == actor && r.Operation == operation && r.TargetId == target && r.Key == key, ct);
        public void RemoveReceipt(OperationReceipt receipt) => db.Remove(receipt);
        public void Add<T>(T entity) where T : class => db.Add(entity);
        public Task Save(CancellationToken ct) => db.SaveChangesAsync(ct);
    }
}
