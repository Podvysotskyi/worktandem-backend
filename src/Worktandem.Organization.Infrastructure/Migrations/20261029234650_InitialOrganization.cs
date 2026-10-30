using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worktandem.Organization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    current_application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    creation_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_companies_id", x => x.id);
                    table.CheckConstraint("ck_company_name", "length(btrim(name, E' \\t\\n\\r')) > 0");
                    table.CheckConstraint("ck_company_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_company_status", "status IN ('Pending', 'Approved', 'Rejected', 'Suspended', 'Archived')");
                });

            migrationBuilder.CreateTable(
                name: "company_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    applicant_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_number = table.Column<int>(type: "integer", nullable: false),
                    company_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    business_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    applicant_role_description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    website = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    legal_registration_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_applications_id", x => x.id);
                    table.UniqueConstraint("ak_company_applications_company_id_id", x => new { x.company_id, x.id });
                    table.UniqueConstraint("ak_company_applications_company_id_id_version", x => new { x.company_id, x.id, x.version });
                    table.CheckConstraint("ck_application_form", "length(btrim(company_name, E' \\t\\n\\r')) BETWEEN 1 AND 200 AND country_code ~ '^[A-Z]{2}$' AND length(btrim(business_description, E' \\t\\n\\r')) BETWEEN 20 AND 2000 AND length(btrim(applicant_role_description, E' \\t\\n\\r')) BETWEEN 2 AND 200");
                    table.CheckConstraint("ck_application_submission", "submission_number > 0");
                    table.ForeignKey(
                        name: "fk_company_applications_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "company_memberships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_memberships_id", x => x.id);
                    table.UniqueConstraint("ak_company_memberships_company_id_id", x => new { x.company_id, x.id });
                    table.CheckConstraint("ck_membership_end", "(status = 'Left' AND ended_at IS NOT NULL AND ended_at >= joined_at) OR (status <> 'Left' AND ended_at IS NULL)");
                    table.CheckConstraint("ck_membership_status", "status IN ('Active', 'Suspended', 'Left')");
                    table.ForeignKey(
                        name: "fk_company_memberships_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "company_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_roles_id", x => x.id);
                    table.UniqueConstraint("ak_company_roles_company_id_id", x => new { x.company_id, x.id });
                    table.CheckConstraint("ck_role_name", "length(btrim(name, E' \\t\\n\\r')) > 0 AND length(btrim(normalized_name, E' \\t\\n\\r')) > 0");
                    table.ForeignKey(
                        name: "fk_company_roles_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewed_application_version = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    platform_authorization_evidence_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    applicant_visible_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    internal_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_decisions_id", x => x.id);
                    table.CheckConstraint("ck_review_outcome", "outcome IN ('Approved', 'Rejected')");
                    table.CheckConstraint("ck_review_reason", "outcome <> 'Rejected' OR (applicant_visible_reason IS NOT NULL AND length(btrim(applicant_visible_reason, E' \\t\\n\\r')) > 0)");
                    table.ForeignKey(
                        name: "fk_review_decisions_company_id_application_id_revi_9817b0d940f3",
                        columns: x => new { x.company_id, x.application_id, x.reviewed_application_version },
                        principalTable: "company_applications",
                        principalColumns: new[] { "company_id", "id", "version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "company_role_assignments",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_role_assignments_company_id_membership_id_role_id", x => new { x.company_id, x.membership_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_company_role_assignments_company_id_membership_id",
                        columns: x => new { x.company_id, x.membership_id },
                        principalTable: "company_memberships",
                        principalColumns: new[] { "company_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_company_role_assignments_company_id_role_id",
                        columns: x => new { x.company_id, x.role_id },
                        principalTable: "company_roles",
                        principalColumns: new[] { "company_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permissions_company_id_role_id_permission_key", x => new { x.company_id, x.role_id, x.permission_key });
                    table.ForeignKey(
                        name: "fk_role_permissions_company_id_role_id",
                        columns: x => new { x.company_id, x.role_id },
                        principalTable: "company_roles",
                        principalColumns: new[] { "company_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_companies_created_by_account_id_creation_operation_id",
                table: "companies",
                columns: new[] { "created_by_account_id", "creation_operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_companies_id_current_application_id",
                table: "companies",
                columns: new[] { "id", "current_application_id" });

            migrationBuilder.CreateIndex(
                name: "ix_companies_slug",
                table: "companies",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_company_applications_company_id_submission_number",
                table: "company_applications",
                columns: new[] { "company_id", "submission_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_company_applications_submitted_at_company_id",
                table: "company_applications",
                columns: new[] { "submitted_at", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ix_company_memberships_account_id_status",
                table: "company_memberships",
                columns: new[] { "account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_company_memberships_company_id_account_id",
                table: "company_memberships",
                columns: new[] { "company_id", "account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_company_role_assignments_company_id_role_id",
                table: "company_role_assignments",
                columns: new[] { "company_id", "role_id" });

            migrationBuilder.CreateIndex(
                name: "ix_company_roles_company_id_normalized_name",
                table: "company_roles",
                columns: new[] { "company_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_decisions_company_id_application_id",
                table: "review_decisions",
                columns: new[] { "company_id", "application_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_decisions_company_id_application_id_revi_5287cdf11c34",
                table: "review_decisions",
                columns: new[] { "company_id", "application_id", "reviewed_application_version" });

            migrationBuilder.CreateIndex(
                name: "ix_review_decisions_company_id_decided_at",
                table: "review_decisions",
                columns: new[] { "company_id", "decided_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_companies_id_current_application_id",
                table: "companies",
                columns: new[] { "id", "current_application_id" },
                principalTable: "company_applications",
                principalColumns: new[] { "company_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_companies_id_current_application_id",
                table: "companies");

            migrationBuilder.DropTable(
                name: "company_role_assignments");

            migrationBuilder.DropTable(
                name: "review_decisions");

            migrationBuilder.DropTable(
                name: "role_permissions");

            migrationBuilder.DropTable(
                name: "company_memberships");

            migrationBuilder.DropTable(
                name: "company_roles");

            migrationBuilder.DropTable(
                name: "company_applications");

            migrationBuilder.DropTable(
                name: "companies");
        }
    }
}
