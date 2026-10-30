using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Worktandem.Identity.Domain;
using Worktandem.Identity.Infrastructure;
using Worktandem.Organization.Domain;
using Xunit;

namespace Worktandem.PersistenceTests;

[Collection("postgres")]
public sealed class PersistenceTests(PostgresFixture postgres)
{
    private static Guid Id() => Guid.CreateVersion7();
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;
    private static Account Account(string name = "Fixture account") => new(Id(), name, AccountStatus.Active, null, Now, Now, Id());
    private static ExternalIdentity Google(Account account, string subject) =>
        new(Id(), account.Id, "Google", "https://accounts.google.com", subject, "fixture@example.invalid", true, null, Now, Now, Id());
    private static Company Company(string slug) => new(Id(), "Fixture company", slug, CompanyStatus.Pending, null, Id(), Id(), Now, Now, Id());
    private static CompanyApplication Application(Company company, int number = 1) =>
        new(Id(), company.Id, Id(), number, company.Name, "US", "A fixture description long enough.", "Owner", null, null, Now, Id());
    private static CompanyMembership Member(Company company, Guid? account = null) =>
        new(Id(), company.Id, account ?? Id(), MembershipStatus.Active, Now, null, Now, Now, Id());
    private static CompanyRole Role(Company company) => new(Id(), company.Id, "Owner", "OWNER", true, Now, Now, Id());

    private static async Task Constraint(DbContext db, object entity, string sqlState)
    {
        db.Add(entity);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Migrations_upgrade_repeat_and_model_match_with_only_an_incomplete_setup_seed()
    {
        var identityConnection = await postgres.CreateDatabase();
        var organizationConnection = await postgres.CreateDatabase();
        await using var identity = PostgresFixture.Identity(identityConnection);
        await using var organization = PostgresFixture.Organization(organizationConnection);
        foreach (DbContext db in new DbContext[] { identity, organization })
        {
            var migrations = db.Database.GetMigrations().ToArray();
            Assert.Equal(2, migrations.Length);
            await db.GetService<IMigrator>().MigrateAsync(migrations[0]);
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            // An idempotent script can also be replayed on the migrated database.
            var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
            await db.Database.OpenConnectionAsync();
            await using (var replay = db.Database.GetDbConnection().CreateCommand())
            {
                replay.CommandText = script;
                await replay.ExecuteNonQueryAsync();
            }
            await db.Database.CloseConnectionAsync();
            // Only empty disposable databases are rolled back; production downgrades are not automatic.
            await db.GetService<IMigrator>().MigrateAsync("0");
            await db.Database.MigrateAsync();
        }
        var setup = await identity.Set<SetupState>().SingleAsync();
        Assert.Equal(SetupStatus.Incomplete, setup.Status);
        Assert.Null(setup.InitialOwnerAccountId);
        Assert.Empty(await identity.Set<Account>().ToListAsync());
        Assert.Empty(await organization.Set<Company>().ToListAsync());
        await using var sql = new NpgsqlConnection(organizationConnection);
        await sql.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('accounts', 'external_identities', 'platform_grants')", sql);
        Assert.Equal(0L, await query.ExecuteScalarAsync());
        // Real separate databases; no Identity SQL objects appear in Organization.
        Assert.NotEqual(new NpgsqlConnectionStringBuilder(identityConnection).Database, new NpgsqlConnectionStringBuilder(organizationConnection).Database);
    }

    [Fact]
    public async Task Google_subject_is_case_sensitive_unique_and_one_google_per_account()
    {
        await using var db = PostgresFixture.Identity(await postgres.CreateDatabase());
        await db.Database.MigrateAsync();
        var a = Account(); var b = Account();
        db.AddRange(a, b, Google(a, "Subject"));
        await db.SaveChangesAsync();
        await Constraint(db, Google(b, "Subject"), PostgresErrorCodes.UniqueViolation);
        await Constraint(db, Google(a, "Another"), PostgresErrorCodes.UniqueViolation);
        db.Add(Google(b, "subject")); // provider subjects are case-sensitive, email collisions do not merge accounts
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<ExternalIdentity>().CountAsync());
        var invalid = new ExternalIdentity(Id(), b.Id, "Google", "https://untrusted.invalid", "x", null, false, null, Now, Now, Id());
        await Constraint(db, invalid, PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Sessions_bind_identity_to_its_account_and_credentials_are_only_digests()
    {
        await using var db = PostgresFixture.Identity(await postgres.CreateDatabase());
        await db.Database.MigrateAsync();
        var a = Account(); var b = Account(); var google = Google(a, "session");
        var browser = new BrowserSession(Id(), new string('a', 64), Now, Now.AddHours(12), null, Id());
        db.AddRange(a, b, google, browser); await db.SaveChangesAsync();
        var auth = Now;
        await Constraint(db, new AccountSession(Id(), browser.Id, b.Id, google.Id, auth, auth, auth.AddMinutes(30), auth.AddHours(12), null, Id()), PostgresErrorCodes.ForeignKeyViolation);
        db.Add(new AccountSession(Id(), browser.Id, a.Id, google.Id, auth, auth, auth.AddMinutes(30), auth.AddHours(12), null, Id()));
        await db.SaveChangesAsync();
        await Constraint(db, new BrowserSession(Id(), "raw-bearer-credential", Now, Now.AddHours(1), null, Id()), PostgresErrorCodes.CheckViolation);
        await Constraint(db, new BrowserSession(Id(), new string('a', 64), Now, Now.AddHours(1), null, Id()), PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Account_versions_reject_lost_updates()
    {
        var connection = await postgres.CreateDatabase();
        await using var first = PostgresFixture.Identity(connection);
        await first.Database.MigrateAsync();
        var account = Account(); first.Add(account); await first.SaveChangesAsync();
        await using var second = PostgresFixture.Identity(connection);
        var stale = await second.Set<Account>().SingleAsync();
        account.Rename("First writer"); await first.SaveChangesAsync();
        stale.Rename("Stale writer");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        Assert.NotEqual(account.Version, stale.Version);
    }

    [Fact]
    public async Task Grant_revocation_requires_both_time_and_actor()
    {
        await using var db = PostgresFixture.Identity(await postgres.CreateDatabase());
        await db.Database.MigrateAsync();
        var owner = Account(); var actor = Account();
        db.AddRange(owner, actor); await db.SaveChangesAsync();
        var granted = Now;
        await Constraint(db, new PlatformGrant(owner.Id, "platform.owner", null, granted, null, actor.Id, Id()), PostgresErrorCodes.CheckViolation);
        await Constraint(db, new PlatformGrant(owner.Id, "platform.owner", null, granted, granted.AddSeconds(1), null, Id()), PostgresErrorCodes.CheckViolation);
        db.Add(new PlatformGrant(owner.Id, "platform.owner", null, granted, granted.AddSeconds(1), actor.Id, Id()));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Suspension_reason_and_history_are_constrained_but_no_status_workflow_is_exposed()
    {
        await using var db = PostgresFixture.Identity(await postgres.CreateDatabase());
        await db.Database.MigrateAsync();
        var target = Account(); var actor = Account();
        db.AddRange(target, actor); await db.SaveChangesAsync();
        await Constraint(db, new AccountStatusTransition(Id(), target.Id, actor.Id, AccountStatus.Active, AccountStatus.Suspended, " \t\r\n", Now, target.Version, Id(), "empty-reason"), PostgresErrorCodes.CheckViolation);
        var transition = new AccountStatusTransition(Id(), target.Id, actor.Id, AccountStatus.Active, AccountStatus.Suspended, "Fixture reason", Now, target.Version, Id(), "suspend");
        db.Add(transition); await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE account_status_transitions SET reason = 'rewritten'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Tenant_keys_reject_foreign_members_roles_current_applications_and_review_versions()
    {
        await using var db = PostgresFixture.Organization(await postgres.CreateDatabase());
        await db.Database.MigrateAsync();
        var a = Company("tenant-a"); var b = Company("tenant-b");
        var member = Member(a); var role = Role(b); var app = Application(a);
        db.AddRange(a, b, member, role, app); await db.SaveChangesAsync();
        await Constraint(db, Member(a, member.AccountId), PostgresErrorCodes.UniqueViolation);
        await Constraint(db, new CompanyRoleAssignment(a.Id, member.Id, role.Id, Now), PostgresErrorCodes.ForeignKeyViolation);
        await Constraint(db, new RolePermission(a.Id, role.Id, "members.read"), PostgresErrorCodes.ForeignKeyViolation);
        var wrongPointer = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET current_application_id={app.Id} WHERE id={b.Id}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, wrongPointer.SqlState);
        await Constraint(db, new ReviewDecision(Id(), a.Id, app.Id, Id(), Id(), ReviewOutcome.Approved, Now, "fixture-evidence", "review", null, null), PostgresErrorCodes.ForeignKeyViolation);
        await Constraint(db, new ReviewDecision(Id(), a.Id, app.Id, Id(), app.Version, ReviewOutcome.Rejected, Now, "fixture-evidence", "review", " \t", null), PostgresErrorCodes.CheckViolation);
        db.Add(new ReviewDecision(Id(), a.Id, app.Id, Id(), app.Version, ReviewOutcome.Approved, Now, "fixture-evidence", "review", null, null));
        await db.SaveChangesAsync();
        await Constraint(db, new ReviewDecision(Id(), a.Id, app.Id, Id(), app.Version, ReviewOutcome.Approved, Now, "fixture-evidence", "again", null, null), PostgresErrorCodes.UniqueViolation);
        await Constraint(db, Application(a), PostgresErrorCodes.UniqueViolation);
        var ownRole = Role(a);
        db.Add(ownRole); await db.SaveChangesAsync();
        db.AddRange(new CompanyRoleAssignment(a.Id, member.Id, ownRole.Id, Now),
            new RolePermission(a.Id, ownRole.Id, "members.read"));
        await db.SaveChangesAsync();
        var restrict = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM company_roles WHERE id={ownRole.Id}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, restrict.SqlState);
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE company_applications SET company_name='changed'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, immutable.SqlState);
    }

    [Fact]
    public async Task Company_version_and_current_application_can_advance_atomically_and_reject_a_stale_writer()
    {
        var connection = await postgres.CreateDatabase();
        await using var first = PostgresFixture.Organization(connection);
        await first.Database.MigrateAsync();
        var company = Company("versioned");
        var application = Application(company);
        first.AddRange(company, application); await first.SaveChangesAsync();
        await using var second = PostgresFixture.Organization(connection);
        var stale = await second.Set<Company>().SingleAsync();
        // Simulate the future authorized write boundary; no approval use case is implemented here.
        first.Entry(company).Property(c => c.CurrentApplicationId).CurrentValue = application.Id;
        first.Entry(company).Property(c => c.Version).CurrentValue = Id();
        await first.SaveChangesAsync();
        second.Entry(stale).Property(c => c.Name).CurrentValue = "Stale edit";
        second.Entry(stale).Property(c => c.Version).CurrentValue = Id();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        Assert.Equal(application.Id, (await first.Set<Company>().AsNoTracking().SingleAsync()).CurrentApplicationId);
    }

    [Fact]
    public async Task Registration_operation_is_durable_and_membership_status_stays_company_local()
    {
        await using var db = PostgresFixture.Organization(await postgres.CreateDatabase());
        await db.Database.MigrateAsync();
        var a = Company("first"); db.Add(a); await db.SaveChangesAsync();
        await Constraint(db, new Company(Id(), "Duplicate", "second", CompanyStatus.Pending, null, a.CreatedByAccountId, a.CreationOperationId, Now, Now, Id()), PostgresErrorCodes.UniqueViolation);
        var b = Company("independent"); var account = Id();
        db.AddRange(b, Member(a, account), new CompanyMembership(Id(), b.Id, account, MembershipStatus.Suspended, Now, null, Now, Now, Id()));
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<CompanyMembership>().CountAsync(m => m.AccountId == account));
        Assert.Single(await db.Set<CompanyMembership>().Where(m => m.AccountId == account && m.Status == MembershipStatus.Active).ToListAsync());
    }

    [Fact]
    public async Task Bootstrap_persistence_fixture_serializes_two_attempts_and_never_reopens()
    {
        var connection = await postgres.CreateDatabase();
        await using (var db = PostgresFixture.Identity(connection)) await db.Database.MigrateAsync();
        // Persistence scaffold only: no Google proof, secret checking, registration or public bootstrap handler.
        var firstLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contenderName = "bootstrap-contender-" + Id();
        async Task<bool> Attempt(bool holdLock)
        {
            var attemptConnection = new NpgsqlConnectionStringBuilder(connection)
            {
                ApplicationName = holdLock ? "bootstrap-first" : contenderName
            }.ConnectionString;
            await using var db = PostgresFixture.Identity(attemptConnection);
            await using var tx = await db.Database.BeginTransactionAsync();
            var setup = (await db.Set<SetupState>().FromSqlRaw("SELECT * FROM setup_state WHERE id='platform' FOR UPDATE").ToListAsync()).Single();
            if (holdLock)
            {
                firstLocked.SetResult();
                await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (setup.Status == SetupStatus.Completed || await db.Set<Account>().AnyAsync()) return false;
            var owner = Account("Fixture owner");
            db.AddRange(owner, Google(owner, "bootstrap-" + owner.Id), new PlatformGrant(owner.Id, "platform.owner", null, Now, null, null, Id()));
            await db.SaveChangesAsync();
            setup.Complete(owner.Id, Now);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return true;
        }
        var first = Attempt(holdLock: true);
        await firstLocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Attempt(holdLock: false);
        try
        {
            // Prove the contender is waiting on PostgreSQL's row lock, not just scheduled later.
            await using var observer = new NpgsqlConnection(connection);
            await observer.OpenAsync();
            var blocked = false;
            for (var attempt = 0; attempt < 100 && !blocked; attempt++)
            {
                await using var locks = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @name AND wait_event_type = 'Lock')", observer);
                locks.Parameters.AddWithValue("name", contenderName);
                blocked = (bool)(await locks.ExecuteScalarAsync())!;
                if (!blocked) await Task.Delay(25);
            }
            Assert.True(blocked, "The competing bootstrap transaction must wait on the held row lock.");
            Assert.False(second.IsCompleted);
        }
        finally { releaseFirst.TrySetResult(); }
        var attempts = await Task.WhenAll(first, second);
        Assert.Equal(1, attempts.Count(x => x));
        await using var verify = PostgresFixture.Identity(connection);
        Assert.Single(await verify.Set<Account>().ToListAsync());
        Assert.Single(await verify.Set<PlatformGrant>().ToListAsync());
        Assert.Equal(SetupStatus.Completed, (await verify.Set<SetupState>().SingleAsync()).Status);
        var reset = await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlRawAsync("UPDATE setup_state SET status='Incomplete', completed_at=NULL, initial_owner_account_id=NULL"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, reset.SqlState);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlRawAsync("DELETE FROM setup_state"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, delete.SqlState);
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlRawAsync("TRUNCATE setup_state"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, truncate.SqlState);
        // Losing all grants and closing the historical owner cannot make setup available again.
        await verify.Database.ExecuteSqlRawAsync("DELETE FROM platform_grants; UPDATE accounts SET status='Closed'");
        Assert.False(await Attempt(holdLock: false));
    }

    [Fact]
    public async Task Bootstrap_fixture_rollback_does_not_leave_partial_owner_or_completed_setup()
    {
        var connection = await postgres.CreateDatabase();
        await using (var db = PostgresFixture.Identity(connection))
        {
            await db.Database.MigrateAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            var owner = Account();
            db.AddRange(owner, Google(owner, "rollback"), new PlatformGrant(owner.Id, "platform.owner", null, Now, null, null, Id()));
            await db.SaveChangesAsync();
            var setup = await db.Set<SetupState>().SingleAsync();
            setup.Complete(owner.Id, Now); await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }
        await using var verify = PostgresFixture.Identity(connection);
        Assert.Empty(await verify.Set<Account>().ToListAsync());
        Assert.Empty(await verify.Set<ExternalIdentity>().ToListAsync());
        Assert.Empty(await verify.Set<PlatformGrant>().ToListAsync());
        Assert.Equal(SetupStatus.Incomplete, (await verify.Set<SetupState>().SingleAsync()).Status);
    }

    [Fact]
    public async Task Database_readiness_requires_migrations_and_does_not_apply_them()
    {
        var connection = await postgres.CreateDatabase();
        await using var factory = new WebApplicationFactory<Identity.Api.Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Identity"] = connection })));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        await using var db = PostgresFixture.Identity(connection);
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
