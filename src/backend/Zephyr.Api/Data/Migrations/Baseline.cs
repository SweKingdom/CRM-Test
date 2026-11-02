using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zephyr.Api.Data.Migrations;

/// <summary>Baseline: extensions, external_id and audit_log (US-129).</summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0001_Baseline")]
public class Baseline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS unaccent;");

        migrationBuilder.CreateTable(
            name: "audit_log",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                user_id = table.Column<Guid>(type: "uuid", nullable: true),
                action = table.Column<string>(type: "text", nullable: false),
                entity_type = table.Column<string>(type: "text", nullable: false),
                entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                detail = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_audit_log", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "external_id",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                created_by = table.Column<Guid>(type: "uuid", nullable: true),
                updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                custom = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                entity_type = table.Column<string>(type: "text", nullable: false),
                entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                system = table.Column<string>(type: "text", nullable: false),
                value = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_external_id", x => x.id);
                table.CheckConstraint("ck_external_id_system", "\"system\" IN ('dynamics', 'fortnox')");
            });

        migrationBuilder.CreateIndex(
            name: "ix_audit_log_entity_type_entity_id_at",
            table: "audit_log",
            columns: new[] { "entity_type", "entity_id", "at" });

        migrationBuilder.CreateIndex(
            name: "ix_external_id_entity_type_entity_id",
            table: "external_id",
            columns: new[] { "entity_type", "entity_id" });

        migrationBuilder.CreateIndex(
            name: "ux_external_id_system_entity_type_value",
            table: "external_id",
            columns: new[] { "system", "entity_type", "value" },
            unique: true);

        // audit_log is append-only: rows can be inserted, never changed or removed.
        migrationBuilder.Sql(
            """
            CREATE FUNCTION audit_log_append_only() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'audit_log is append-only';
            END;
            $$ LANGUAGE plpgsql;
            """);
        migrationBuilder.Sql(
            """
            CREATE TRIGGER trg_audit_log_append_only BEFORE UPDATE OR DELETE ON audit_log
            FOR EACH ROW EXECUTE FUNCTION audit_log_append_only();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Extensions stay.
        migrationBuilder.DropTable(name: "external_id");
        migrationBuilder.DropTable(name: "audit_log");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_log_append_only();");
    }
}
