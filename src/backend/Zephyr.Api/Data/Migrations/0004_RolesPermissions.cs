using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zephyr.Api.Data.Migrations;

/// <summary>
/// Roles and the permission matrix (US-143) plus <c>app_user.manager_id</c>, which defines a team. Rows come from
/// <c>Permissions/permission-matrix.csv</c> through <c>PermissionSeeder</c>; nothing edits them at run time.
/// </summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0004_RolesPermissions")]
public class AddRolesPermissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("create table role (key text constraint pk_role primary key, label text not null);");
        // A row with scope 'none' is the same as no row: the engine treats an absent cell as none.
        migrationBuilder.Sql(
            """
            create table role_permission (
                role_key text not null constraint fk_role_permission_role references role (key),
                entity text not null,
                action text not null constraint ck_role_permission_action check (action in ('read', 'create', 'update', 'delete', 'approve')),
                scope text not null constraint ck_role_permission_scope check (scope in ('none', 'own', 'team', 'country', 'org')),
                constraint pk_role_permission primary key (role_key, entity, action)
            );
            """);
        migrationBuilder.Sql(
            "alter table app_user add column manager_id uuid null constraint fk_app_user_manager references app_user (id), " +
            "add constraint ck_app_user_manager_not_self check (manager_id <> id);");
        migrationBuilder.Sql("create index ix_app_user_manager_id on app_user (manager_id) where manager_id is not null;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop index if exists ix_app_user_manager_id;");
        migrationBuilder.Sql("alter table app_user drop constraint if exists ck_app_user_manager_not_self, drop column if exists manager_id;");
        migrationBuilder.Sql("drop table if exists role_permission;");
        migrationBuilder.Sql("drop table if exists role;");
    }
}
