using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worktandem.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BootstrapAndSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "consumed_at",
                table: "pending_logins",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "authentication_revoked_at",
                table: "accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "evidence_observed_at",
                table: "account_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "mfa_observed",
                table: "account_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_authenticated_at",
                table: "account_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "authentication_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    browser_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pending_login_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result_version = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_authentication_audit_id", x => x.id);
                    table.CheckConstraint("ck_auth_audit_event", "event_name IN ('bootstrap.completed', 'account.registered', 'session.created', 'session.logged_out', 'container.logged_out', 'authority.validated')");
                    table.ForeignKey(
                        name: "fk_authentication_audit_actor_account_id",
                        column: x => x.actor_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_authentication_audit_browser_session_id",
                        column: x => x.browser_session_id,
                        principalTable: "browser_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "setup_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pending_login_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_failure = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_setup_attempts_id", x => x.id);
                    table.CheckConstraint("ck_attempt_source", "source_hash ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_pending_consumption",
                table: "pending_logins",
                sql: "consumed_at IS NULL OR (validated_at IS NOT NULL AND consumed_at >= validated_at AND consumed_at < expires_at)");

            migrationBuilder.CreateIndex(
                name: "ix_account_sessions_browser_session_id_account_id",
                table: "account_sessions",
                columns: new[] { "browser_session_id", "account_id" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_authentication_audit_actor_account_id_occurred_at_id",
                table: "authentication_audit",
                columns: new[] { "actor_account_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_authentication_audit_browser_session_id",
                table: "authentication_audit",
                column: "browser_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_setup_attempts_occurred_at",
                table: "setup_attempts",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_setup_attempts_pending_login_id_occurred_at",
                table: "setup_attempts",
                columns: new[] { "pending_login_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_setup_attempts_source_hash_occurred_at",
                table: "setup_attempts",
                columns: new[] { "source_hash", "occurred_at" });
            migrationBuilder.Sql("""
                CREATE TRIGGER protect_history_rows BEFORE UPDATE OR DELETE ON authentication_audit
                FOR EACH ROW EXECUTE FUNCTION reject_history_mutation();
                CREATE TRIGGER protect_history_truncate BEFORE TRUNCATE ON authentication_audit
                FOR EACH STATEMENT EXECUTE FUNCTION reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authentication_audit");

            migrationBuilder.DropTable(
                name: "setup_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pending_consumption",
                table: "pending_logins");

            migrationBuilder.DropIndex(
                name: "ix_account_sessions_browser_session_id_account_id",
                table: "account_sessions");

            migrationBuilder.DropColumn(
                name: "consumed_at",
                table: "pending_logins");

            migrationBuilder.DropColumn(
                name: "authentication_revoked_at",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "evidence_observed_at",
                table: "account_sessions");

            migrationBuilder.DropColumn(
                name: "mfa_observed",
                table: "account_sessions");

            migrationBuilder.DropColumn(
                name: "provider_authenticated_at",
                table: "account_sessions");
        }
    }
}
