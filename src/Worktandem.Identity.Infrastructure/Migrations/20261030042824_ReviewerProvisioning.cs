using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worktandem.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReviewerProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_auth_audit_event",
                table: "authentication_audit");

            migrationBuilder.AddColumn<string>(
                name: "capability_key",
                table: "authentication_audit",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "prior_version",
                table: "authentication_audit",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "target_account_id",
                table: "authentication_audit",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_creation_operation_id",
                table: "accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "grant_receipts",
                columns: table => new
                {
                    actor_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    result_version = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grant_receipts_actor_account_id_key", x => new { x.actor_account_id, x.key });
                    table.ForeignKey(
                        name: "fk_grant_receipts_actor_account_id",
                        column: x => x.actor_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_authentication_audit_target_account_id",
                table: "authentication_audit",
                column: "target_account_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_auth_audit_event",
                table: "authentication_audit",
                sql: "event_name IN ('bootstrap.completed', 'account.registered', 'session.created', 'session.logged_out', 'container.logged_out', 'authority.validated', 'company.operation_issued', 'reviewer.granted', 'reviewer.revoked')");

            migrationBuilder.CreateIndex(
                name: "ix_grant_receipts_expires_at",
                table: "grant_receipts",
                column: "expires_at");

            migrationBuilder.AddForeignKey(
                name: "fk_authentication_audit_target_account_id",
                table: "authentication_audit",
                column: "target_account_id",
                principalTable: "accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_authentication_audit_target_account_id",
                table: "authentication_audit");

            migrationBuilder.DropTable(
                name: "grant_receipts");

            migrationBuilder.DropIndex(
                name: "ix_authentication_audit_target_account_id",
                table: "authentication_audit");

            migrationBuilder.DropCheckConstraint(
                name: "ck_auth_audit_event",
                table: "authentication_audit");

            migrationBuilder.DropColumn(
                name: "capability_key",
                table: "authentication_audit");

            migrationBuilder.DropColumn(
                name: "prior_version",
                table: "authentication_audit");

            migrationBuilder.DropColumn(
                name: "target_account_id",
                table: "authentication_audit");

            migrationBuilder.DropColumn(
                name: "company_creation_operation_id",
                table: "accounts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_auth_audit_event",
                table: "authentication_audit",
                sql: "event_name IN ('bootstrap.completed', 'account.registered', 'session.created', 'session.logged_out', 'container.logged_out', 'authority.validated')");
        }
    }
}
