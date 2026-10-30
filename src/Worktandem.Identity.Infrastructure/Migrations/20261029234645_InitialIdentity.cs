using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worktandem.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    initial_registration_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts_id", x => x.id);
                    table.CheckConstraint("ck_accounts_name", "length(btrim(display_name, E' \\t\\n\\r')) BETWEEN 1 AND 200");
                    table.CheckConstraint("ck_accounts_status", "status IN ('Active', 'Suspended', 'Closed')");
                });

            migrationBuilder.CreateTable(
                name: "browser_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_browser_sessions_id", x => x.id);
                    table.CheckConstraint("ck_browser_digest", "credential_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_browser_lifetime", "expires_at > created_at AND (revoked_at IS NULL OR revoked_at >= created_at)");
                });

            migrationBuilder.CreateTable(
                name: "account_status_transitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    new_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expected_account_version = table.Column<Guid>(type: "uuid", nullable: false),
                    result_account_version = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_status_transitions_id", x => x.id);
                    table.CheckConstraint("ck_status_transition", "previous_status IN ('Active', 'Suspended', 'Closed') AND new_status IN ('Active', 'Suspended', 'Closed') AND previous_status <> new_status AND expected_account_version <> result_account_version");
                    table.CheckConstraint("ck_suspension_reason", "new_status <> 'Suspended' OR (reason IS NOT NULL AND length(btrim(reason, E' \\t\\n\\r')) > 0)");
                    table.ForeignKey(
                        name: "fk_account_status_transitions_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_account_status_transitions_actor_account_id",
                        column: x => x.actor_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "external_identities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    issuer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    email_verified = table.Column<bool>(type: "boolean", nullable: false),
                    hosted_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    associated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_identities_id", x => x.id);
                    table.UniqueConstraint("ak_external_identities_account_id_id", x => new { x.account_id, x.id });
                    table.CheckConstraint("ck_identity_google", "provider = 'Google' AND issuer = 'https://accounts.google.com' AND length(subject) > 0");
                    table.ForeignKey(
                        name: "fk_external_identities_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "platform_grants",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    granted_by_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_grants_account_id_capability_key", x => new { x.account_id, x.capability_key });
                    table.CheckConstraint("ck_grant_revocation", "(revoked_at IS NULL AND revoked_by_account_id IS NULL) OR (revoked_at IS NOT NULL AND revoked_at >= granted_at AND revoked_by_account_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_platform_grants_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_platform_grants_granted_by_account_id",
                        column: x => x.granted_by_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_platform_grants_revoked_by_account_id",
                        column: x => x.revoked_by_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "setup_state",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    initial_owner_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_setup_state_id", x => x.id);
                    table.CheckConstraint("ck_setup_completion", "(status = 'Incomplete' AND completed_at IS NULL AND initial_owner_account_id IS NULL) OR (status = 'Completed' AND completed_at IS NOT NULL AND initial_owner_account_id IS NOT NULL)");
                    table.CheckConstraint("ck_setup_singleton", "id = 'platform'");
                    table.ForeignKey(
                        name: "fk_setup_state_initial_owner_account_id",
                        column: x => x.initial_owner_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    browser_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authenticated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idle_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_sessions_id", x => x.id);
                    table.CheckConstraint("ck_session_lifetime", "last_seen_at >= authenticated_at AND idle_expires_at > last_seen_at AND expires_at > authenticated_at AND idle_expires_at <= expires_at AND (revoked_at IS NULL OR revoked_at >= authenticated_at)");
                    table.ForeignKey(
                        name: "fk_account_sessions_account_id_external_identity_id",
                        columns: x => new { x.account_id, x.external_identity_id },
                        principalTable: "external_identities",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_account_sessions_browser_session_id",
                        column: x => x.browser_session_id,
                        principalTable: "browser_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "setup_state",
                columns: new[] { "id", "completed_at", "initial_owner_account_id", "status", "version" },
                values: new object[] { "platform", null, null, "Incomplete", new Guid("01900000-0000-7000-8000-000000000001") });

            migrationBuilder.CreateIndex(
                name: "ix_account_sessions_account_id_external_identity_id",
                table: "account_sessions",
                columns: new[] { "account_id", "external_identity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_account_sessions_account_id_revoked_at",
                table: "account_sessions",
                columns: new[] { "account_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_account_sessions_browser_session_id_revoked_at",
                table: "account_sessions",
                columns: new[] { "browser_session_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_account_sessions_expires_at",
                table: "account_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_account_status_transitions_account_id_occurred_at_id",
                table: "account_status_transitions",
                columns: new[] { "account_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_account_status_transitions_actor_account_id_operation_id",
                table: "account_status_transitions",
                columns: new[] { "actor_account_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_browser_sessions_credential_digest",
                table: "browser_sessions",
                column: "credential_digest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_browser_sessions_expires_at",
                table: "browser_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_account_id",
                table: "external_identities",
                column: "account_id",
                unique: true,
                filter: "provider = 'Google'");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_issuer_subject",
                table: "external_identities",
                columns: new[] { "issuer", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_platform_grants_capability_key_revoked_at",
                table: "platform_grants",
                columns: new[] { "capability_key", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_platform_grants_granted_by_account_id",
                table: "platform_grants",
                column: "granted_by_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_platform_grants_revoked_by_account_id",
                table: "platform_grants",
                column: "revoked_by_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_setup_state_initial_owner_account_id",
                table: "setup_state",
                column: "initial_owner_account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_sessions");

            migrationBuilder.DropTable(
                name: "account_status_transitions");

            migrationBuilder.DropTable(
                name: "platform_grants");

            migrationBuilder.DropTable(
                name: "setup_state");

            migrationBuilder.DropTable(
                name: "external_identities");

            migrationBuilder.DropTable(
                name: "browser_sessions");

            migrationBuilder.DropTable(
                name: "accounts");
        }
    }
}
