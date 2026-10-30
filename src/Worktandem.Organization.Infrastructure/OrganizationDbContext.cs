using Microsoft.EntityFrameworkCore;
using Worktandem.Organization.Domain;

namespace Worktandem.Organization.Infrastructure;

public sealed class OrganizationDbContext(DbContextOptions<OrganizationDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {

        var company = model.Entity<Company>();
        company.ToTable("companies", t =>
        {
            t.HasCheckConstraint("ck_company_status", "status IN ('Pending', 'Approved', 'Rejected', 'Suspended', 'Archived')");
            t.HasCheckConstraint("ck_company_name", "length(btrim(name, E' \\t\\n\\r')) > 0");
            t.HasCheckConstraint("ck_company_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
        });
        company.Property(x => x.Name).HasMaxLength(200);
        company.Property(x => x.Slug).HasMaxLength(100).UseCollation("C");
        company.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        company.HasIndex(x => x.Slug).IsUnique();
        company.HasIndex(x => new { x.CreatedByAccountId, x.CreationOperationId }).IsUnique();
        company.HasOne<CompanyApplication>().WithMany().HasForeignKey(x => new { x.Id, x.CurrentApplicationId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var application = model.Entity<CompanyApplication>();
        application.ToTable("company_applications", t =>
        {
            t.HasCheckConstraint("ck_application_submission", "submission_number > 0");
            t.HasCheckConstraint("ck_application_form", "length(btrim(company_name, E' \\t\\n\\r')) BETWEEN 1 AND 200 AND country_code ~ '^[A-Z]{2}$' AND length(btrim(business_description, E' \\t\\n\\r')) BETWEEN 20 AND 2000 AND length(btrim(applicant_role_description, E' \\t\\n\\r')) BETWEEN 2 AND 200");
        });
        application.Property(x => x.CompanyName).HasMaxLength(200);
        application.Property(x => x.CountryCode).HasMaxLength(2);
        application.Property(x => x.BusinessDescription).HasMaxLength(2000);
        application.Property(x => x.ApplicantRoleDescription).HasMaxLength(200);
        application.Property(x => x.Website).HasMaxLength(2048);
        application.Property(x => x.LegalRegistrationId).HasMaxLength(200);
        application.HasAlternateKey(x => new { x.CompanyId, x.Id });
        application.HasAlternateKey(x => new { x.CompanyId, x.Id, x.Version });
        application.HasIndex(x => new { x.CompanyId, x.SubmissionNumber }).IsUnique();
        application.HasIndex(x => new { x.SubmittedAt, x.CompanyId });
        application.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);

        var review = model.Entity<ReviewDecision>();
        review.ToTable("review_decisions", t =>
        {
            t.HasCheckConstraint("ck_review_outcome", "outcome IN ('Approved', 'Rejected')");
            t.HasCheckConstraint("ck_review_reason", "outcome <> 'Rejected' OR (applicant_visible_reason IS NOT NULL AND length(btrim(applicant_visible_reason, E' \\t\\n\\r')) > 0)");
        });
        review.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
        review.Property(x => x.PlatformAuthorizationEvidenceRef).HasMaxLength(200);
        review.Property(x => x.IdempotencyKey).HasMaxLength(100);
        review.Property(x => x.ApplicantVisibleReason).HasMaxLength(2000);
        review.Property(x => x.InternalNote).HasMaxLength(2000);
        review.HasOne<CompanyApplication>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ApplicationId, x.ReviewedApplicationVersion }).HasPrincipalKey(x => new { x.CompanyId, x.Id, x.Version }).OnDelete(DeleteBehavior.Restrict);
        review.HasIndex(x => new { x.CompanyId, x.ApplicationId }).IsUnique();
        review.HasIndex(x => new { x.CompanyId, x.DecidedAt });

        var member = model.Entity<CompanyMembership>();
        member.ToTable("company_memberships", t =>
        {
            t.HasCheckConstraint("ck_membership_status", "status IN ('Active', 'Suspended', 'Left')");
            t.HasCheckConstraint("ck_membership_end", "(status = 'Left' AND ended_at IS NOT NULL AND ended_at >= joined_at) OR (status <> 'Left' AND ended_at IS NULL)");
        });
        member.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        member.HasAlternateKey(x => new { x.CompanyId, x.Id });
        member.HasIndex(x => new { x.CompanyId, x.AccountId }).IsUnique();
        member.HasIndex(x => new { x.AccountId, x.Status });
        member.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);

        var role = model.Entity<CompanyRole>();
        role.ToTable("company_roles", t => t.HasCheckConstraint("ck_role_name", "length(btrim(name, E' \\t\\n\\r')) > 0 AND length(btrim(normalized_name, E' \\t\\n\\r')) > 0"));
        role.Property(x => x.Name).HasMaxLength(100);
        role.Property(x => x.NormalizedName).HasMaxLength(100).UseCollation("C");
        role.HasAlternateKey(x => new { x.CompanyId, x.Id });
        role.HasIndex(x => new { x.CompanyId, x.NormalizedName }).IsUnique();
        role.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);

        var permission = model.Entity<RolePermission>();
        permission.ToTable("role_permissions");
        permission.HasKey(x => new { x.CompanyId, x.RoleId, x.PermissionKey });
        permission.Property(x => x.PermissionKey).HasMaxLength(100);
        permission.HasOne<CompanyRole>().WithMany().HasForeignKey(x => new { x.CompanyId, x.RoleId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var assignment = model.Entity<CompanyRoleAssignment>();
        assignment.ToTable("company_role_assignments");
        assignment.HasKey(x => new { x.CompanyId, x.MembershipId, x.RoleId });
        assignment.HasOne<CompanyMembership>().WithMany().HasForeignKey(x => new { x.CompanyId, x.MembershipId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        assignment.HasOne<CompanyRole>().WithMany().HasForeignKey(x => new { x.CompanyId, x.RoleId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var receipt = model.Entity<OperationReceipt>();
        receipt.ToTable("operation_receipts");
        receipt.HasKey(x => new { x.ActorAccountId, x.Operation, x.TargetId, x.Key });
        receipt.Property(x => x.Operation).HasMaxLength(32);
        receipt.Property(x => x.Key).HasMaxLength(100).UseCollation("C");
        receipt.Property(x => x.PayloadHash).HasMaxLength(64);
        receipt.Property(x => x.ResultJson).HasColumnType("jsonb");
        receipt.HasIndex(x => x.ExpiresAt);

        var audit = model.Entity<CompanyAudit>();
        audit.ToTable("company_audit");
        audit.Property(x => x.EventName).HasMaxLength(64);
        audit.Property(x => x.CorrelationId).HasMaxLength(100);
        audit.HasOne<CompanyApplication>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ApplicationId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        audit.HasIndex(x => new { x.CompanyId, x.OccurredAt, x.Id });

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
