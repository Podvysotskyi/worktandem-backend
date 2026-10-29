using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worktandem.Identity.Infrastructure.Migrations;

public partial class ProtectIdentityHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        CREATE FUNCTION reject_history_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Historical records are immutable' USING ERRCODE = '23514';
        END;
        $$;
        CREATE TRIGGER protect_history_rows BEFORE UPDATE OR DELETE ON account_status_transitions FOR EACH ROW EXECUTE FUNCTION reject_history_mutation();
        CREATE TRIGGER protect_history_truncate BEFORE TRUNCATE ON account_status_transitions FOR EACH STATEMENT EXECUTE FUNCTION reject_history_mutation();
        CREATE FUNCTION protect_platform_setup() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP IN ('DELETE', 'TRUNCATE') THEN
                RAISE EXCEPTION 'Setup marker must survive cleanup' USING ERRCODE = '23514';
            END IF;
            IF OLD.status = 'Completed' AND NEW IS DISTINCT FROM OLD THEN
                RAISE EXCEPTION 'Completed setup is permanent' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER protect_setup_rows BEFORE UPDATE OR DELETE ON setup_state FOR EACH ROW EXECUTE FUNCTION protect_platform_setup();
        CREATE TRIGGER protect_setup_truncate BEFORE TRUNCATE ON setup_state FOR EACH STATEMENT EXECUTE FUNCTION protect_platform_setup();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DROP TRIGGER protect_setup_truncate ON setup_state;
        DROP TRIGGER protect_setup_rows ON setup_state;
        DROP FUNCTION protect_platform_setup();
        DROP TRIGGER protect_history_truncate ON account_status_transitions;
        DROP TRIGGER protect_history_rows ON account_status_transitions;
        DROP FUNCTION reject_history_mutation();
        """);
}
