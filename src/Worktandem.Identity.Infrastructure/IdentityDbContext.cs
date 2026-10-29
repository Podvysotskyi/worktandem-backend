using Microsoft.EntityFrameworkCore;
using Worktandem.Identity.Domain;

namespace Worktandem.Identity.Infrastructure;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {

        var account = model.Entity<Account>();
        account.ToTable("accounts", t =>
        {
            t.HasCheckConstraint("ck_accounts_status", "status IN ('Active', 'Suspended', 'Closed')");
            t.HasCheckConstraint("ck_accounts_name", "length(btrim(display_name, E' \\t\\n\\r')) BETWEEN 1 AND 200");
        });
        account.Property(x => x.DisplayName).HasMaxLength(200);
        account.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);

        var identity = model.Entity<ExternalIdentity>();
        identity.ToTable("external_identities", t =>
        {
            t.HasCheckConstraint("ck_identity_google", "provider = 'Google' AND issuer = 'https://accounts.google.com' AND length(subject) > 0");
        });
        identity.Property(x => x.Provider).HasMaxLength(32);
        identity.Property(x => x.Issuer).HasMaxLength(256);
        identity.Property(x => x.Subject).HasMaxLength(255).UseCollation("C");
        identity.Property(x => x.Email).HasMaxLength(320);
        identity.Property(x => x.HostedDomain).HasMaxLength(253);
        identity.HasIndex(x => new { x.Issuer, x.Subject }).IsUnique();
        identity.HasIndex(x => x.AccountId).IsUnique().HasFilter("provider = 'Google'");
        identity.HasAlternateKey(x => new { x.AccountId, x.Id });
        identity.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);

        var setup = model.Entity<SetupState>();
        setup.ToTable("setup_state", t =>
        {
            t.HasCheckConstraint("ck_setup_singleton", "id = 'platform'");
            t.HasCheckConstraint("ck_setup_completion", "(status = 'Incomplete' AND completed_at IS NULL AND initial_owner_account_id IS NULL) OR (status = 'Completed' AND completed_at IS NOT NULL AND initial_owner_account_id IS NOT NULL)");
        });
        setup.Property(x => x.Id).HasMaxLength(32);
        setup.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        setup.HasOne<Account>().WithMany().HasForeignKey(x => x.InitialOwnerAccountId).OnDelete(DeleteBehavior.Restrict);
        setup.HasData(new SetupState("platform", SetupStatus.Incomplete, null, null, new Guid("01900000-0000-7000-8000-000000000001")));

        var grant = model.Entity<PlatformGrant>();
        grant.ToTable("platform_grants", t => t.HasCheckConstraint("ck_grant_revocation", "(revoked_at IS NULL AND revoked_by_account_id IS NULL) OR (revoked_at IS NOT NULL AND revoked_at >= granted_at AND revoked_by_account_id IS NOT NULL)"));
        grant.HasKey(x => new { x.AccountId, x.CapabilityKey });
        grant.Property(x => x.CapabilityKey).HasMaxLength(100);
        grant.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        grant.HasOne<Account>().WithMany().HasForeignKey(x => x.GrantedByAccountId).OnDelete(DeleteBehavior.Restrict);
        grant.HasOne<Account>().WithMany().HasForeignKey(x => x.RevokedByAccountId).OnDelete(DeleteBehavior.Restrict);
        grant.HasIndex(x => new { x.CapabilityKey, x.RevokedAt });

        var browser = model.Entity<BrowserSession>();
        browser.ToTable("browser_sessions", t =>
        {
            t.HasCheckConstraint("ck_browser_lifetime", "expires_at > created_at AND (revoked_at IS NULL OR revoked_at >= created_at)");
            t.HasCheckConstraint("ck_browser_digest", "credential_digest ~ '^[0-9a-f]{64}$'");
        });
        browser.Property(x => x.CredentialDigest).HasMaxLength(64).UseCollation("C");
        browser.HasIndex(x => x.CredentialDigest).IsUnique();
        browser.HasIndex(x => x.ExpiresAt);

        var session = model.Entity<AccountSession>();
        session.ToTable("account_sessions", t => t.HasCheckConstraint("ck_session_lifetime", "last_seen_at >= authenticated_at AND idle_expires_at > last_seen_at AND expires_at > authenticated_at AND idle_expires_at <= expires_at AND (revoked_at IS NULL OR revoked_at >= authenticated_at)"));
        session.HasOne<BrowserSession>().WithMany().HasForeignKey(x => x.BrowserSessionId).OnDelete(DeleteBehavior.Restrict);
        session.HasOne<ExternalIdentity>().WithMany().HasForeignKey(x => new { x.AccountId, x.ExternalIdentityId }).HasPrincipalKey(x => new { x.AccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        session.HasIndex(x => new { x.AccountId, x.RevokedAt });
        session.HasIndex(x => new { x.BrowserSessionId, x.RevokedAt });
        session.HasIndex(x => x.ExpiresAt);

        var transition = model.Entity<AccountStatusTransition>();
        transition.ToTable("account_status_transitions", t =>
        {
            t.HasCheckConstraint("ck_status_transition", "previous_status IN ('Active', 'Suspended', 'Closed') AND new_status IN ('Active', 'Suspended', 'Closed') AND previous_status <> new_status AND expected_account_version <> result_account_version");
            t.HasCheckConstraint("ck_suspension_reason", "new_status <> 'Suspended' OR (reason IS NOT NULL AND length(btrim(reason, E' \\t\\n\\r')) > 0)");
        });
        transition.Property(x => x.PreviousStatus).HasConversion<string>().HasMaxLength(16);
        transition.Property(x => x.NewStatus).HasConversion<string>().HasMaxLength(16);
        transition.Property(x => x.Reason).HasMaxLength(2000);
        transition.Property(x => x.OperationId).HasMaxLength(100);
        transition.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        transition.HasOne<Account>().WithMany().HasForeignKey(x => x.ActorAccountId).OnDelete(DeleteBehavior.Restrict);
        transition.HasIndex(x => new { x.AccountId, x.OccurredAt, x.Id });
        transition.HasIndex(x => new { x.ActorAccountId, x.OperationId }).IsUnique();

        // Service-local relational naming. No cross-service entity registration or shared model.
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Snake(property.Name));
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    property.SetColumnType("timestamp with time zone");
                if (property.Name == "Version") property.IsConcurrencyToken = true;
            }
            foreach (var key in entity.GetKeys())
                key.SetName(ShortName((key.IsPrimaryKey() ? "pk_" : "ak_") + entity.GetTableName() + "_" + string.Join("_", key.Properties.Select(p => Snake(p.Name)))));
            foreach (var index in entity.GetIndexes())
                index.SetDatabaseName(ShortName("ix_" + entity.GetTableName() + "_" + string.Join("_", index.Properties.Select(p => Snake(p.Name)))));
            foreach (var fk in entity.GetForeignKeys())
                fk.SetConstraintName(ShortName("fk_" + entity.GetTableName() + "_" + string.Join("_", fk.Properties.Select(p => Snake(p.Name)))));
        }
    }

    private static string Snake(string value) =>
        string.Concat(value.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    // PostgreSQL identifiers are limited to 63 bytes. Stable hash avoids truncated-name collisions.
    private static string ShortName(string name) => name.Length <= 63 ? name :
        name[..50] + "_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
}
