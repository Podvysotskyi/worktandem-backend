using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worktandem.Organization.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompanyOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "company_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    prior_version = table.Column<Guid>(type: "uuid", nullable: true),
                    result_version = table.Column<Guid>(type: "uuid", nullable: false),
                    authority_evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_audit_id", x => x.id);
                    table.ForeignKey(
                        name: "fk_company_audit_company_id_application_id",
                        columns: x => new { x.company_id, x.application_id },
                        principalTable: "company_applications",
                        principalColumns: new[] { "company_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "operation_receipts",
                columns: table => new
                {
                    actor_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operation_receipts_actor_account_id_operation_target_id_key", x => new { x.actor_account_id, x.operation, x.target_id, x.key });
                });

            migrationBuilder.CreateIndex(
                name: "ix_company_audit_company_id_application_id",
                table: "company_audit",
                columns: new[] { "company_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "ix_company_audit_company_id_occurred_at_id",
                table: "company_audit",
                columns: new[] { "company_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_operation_receipts_expires_at",
                table: "operation_receipts",
                column: "expires_at");
            migrationBuilder.Sql("""
                CREATE TRIGGER protect_history_rows BEFORE UPDATE OR DELETE ON company_audit FOR EACH ROW EXECUTE FUNCTION reject_history_mutation();
                CREATE TRIGGER protect_history_truncate BEFORE TRUNCATE ON company_audit FOR EACH STATEMENT EXECUTE FUNCTION reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "company_audit");

            migrationBuilder.DropTable(
                name: "operation_receipts");
        }
    }
}
