using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worktandem.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PendingGoogleLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_logins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    browser_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    return_path = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    issuer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true, collation: "C"),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    email_verified = table.Column<bool>(type: "boolean", nullable: false),
                    hosted_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    provider_authenticated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    mfa_observed = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_logins_id", x => x.id);
                    table.CheckConstraint("ck_pending_evidence", "(validated_at IS NULL AND issuer IS NULL AND subject IS NULL AND email IS NULL AND hosted_domain IS NULL AND provider_authenticated_at IS NULL AND NOT mfa_observed AND NOT email_verified) OR (validated_at IS NOT NULL AND issuer IS NOT NULL AND issuer = 'https://accounts.google.com' AND subject IS NOT NULL AND length(subject) > 0)");
                    table.CheckConstraint("ck_pending_lifetime", "expires_at > created_at AND (validated_at IS NULL OR validated_at >= created_at)");
                    table.CheckConstraint("ck_pending_purpose", "purpose IN ('Bootstrap', 'Login', 'Register', 'AddAccount')");
                    table.CheckConstraint("ck_pending_return", "return_path IN ('/', '/setup', '/auth/continue')");
                    table.ForeignKey(
                        name: "fk_pending_logins_browser_session_id",
                        column: x => x.browser_session_id,
                        principalTable: "browser_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pending_logins_browser_session_id_expires_at",
                table: "pending_logins",
                columns: new[] { "browser_session_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pending_logins_expires_at",
                table: "pending_logins",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_logins");
        }
    }
}
