using Microsoft.EntityFrameworkCore;
using Worktandem.Identity.Application;
using Worktandem.Identity.Domain;

namespace Worktandem.Identity.Infrastructure;

public sealed class PostgresSetupAttemptBudget(IdentityDbContext database, TimeProvider clock) : ISetupAttemptBudget
{
    public async Task<Guid> Reserve(Guid pendingLoginId, string sourceHash, CancellationToken cancellationToken)
    {
        if (sourceHash.Length != 64 || sourceHash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new AuthenticationFailure("invalid_setup", 409);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // One deployment-wide lock makes rolling-window reservations atomic across replicas.
        await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(580831753901021)", cancellationToken);
        var now = clock.GetUtcNow();
        await database.Set<SetupAttempt>().Where(a => a.OccurredAt <= now.AddMinutes(-10)).ExecuteDeleteAsync(cancellationToken);
        if (await database.Set<SetupAttempt>().CountAsync(a => a.OccurredAt > now.AddMinutes(-1), cancellationToken) >= 30 ||
            await database.Set<SetupAttempt>().CountAsync(a => a.IsFailure && a.PendingLoginId == pendingLoginId, cancellationToken) >= 5 ||
            await database.Set<SetupAttempt>().CountAsync(a => a.IsFailure && a.SourceHash == sourceHash, cancellationToken) >= 5)
            throw new AuthenticationFailure("setup_rate_limited", 429);
        var attempt = new SetupAttempt(Guid.CreateVersion7(), pendingLoginId, sourceHash, now, Guid.CreateVersion7());
        database.Add(attempt);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return attempt.Id;
    }
}
