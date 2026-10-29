using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worktandem.Organization.Infrastructure.Migrations;

public partial class ProtectOrganizationHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        CREATE FUNCTION reject_history_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Historical records are immutable' USING ERRCODE = '23514';
        END;
        $$;
        CREATE TRIGGER protect_history_rows BEFORE UPDATE OR DELETE ON company_applications FOR EACH ROW EXECUTE FUNCTION reject_history_mutation();
        CREATE TRIGGER protect_history_truncate BEFORE TRUNCATE ON company_applications FOR EACH STATEMENT EXECUTE FUNCTION reject_history_mutation();
        CREATE TRIGGER protect_history_rows BEFORE UPDATE OR DELETE ON review_decisions FOR EACH ROW EXECUTE FUNCTION reject_history_mutation();
        CREATE TRIGGER protect_history_truncate BEFORE TRUNCATE ON review_decisions FOR EACH STATEMENT EXECUTE FUNCTION reject_history_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DROP TRIGGER protect_history_truncate ON company_applications;
        DROP TRIGGER protect_history_rows ON company_applications;
        DROP TRIGGER protect_history_truncate ON review_decisions;
        DROP TRIGGER protect_history_rows ON review_decisions;
        DROP FUNCTION reject_history_mutation();
        """);
}
