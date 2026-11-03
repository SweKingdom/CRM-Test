using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zephyr.Api.Data.Migrations;

/// <summary>
/// Per-project time approvers and approval delegation (US-145). Both tables are used through SQL only, like the permission tables.
/// <c>project_approver.project_id</c> has no foreign key yet: the project register (US-161) adds the key and the PM column.
/// </summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0005_Approvers")]
public class AddApprovers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Removing an approver is a hard delete, so this table has no deleted_at; it keeps who added the row and when.
        migrationBuilder.Sql(
            """
            create table project_approver (
                project_id uuid not null,
                user_id uuid not null constraint fk_project_approver_user references app_user (id),
                created_at timestamptz not null default now(),
                created_by uuid null,
                constraint pk_project_approver primary key (project_id, user_id)
            );
            """);
        migrationBuilder.Sql("create index ix_project_approver_user_id on project_approver (user_id);");
        migrationBuilder.Sql(
            """
            create table approval_delegation (
                id uuid constraint pk_approval_delegation primary key default gen_random_uuid(),
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                created_by uuid null,
                updated_by uuid null,
                deleted_at timestamptz null,
                custom jsonb not null default '{}'::jsonb,
                delegator_id uuid not null constraint fk_approval_delegation_delegator references app_user (id),
                delegate_id uuid not null constraint fk_approval_delegation_delegate references app_user (id),
                from_date date not null,
                to_date date not null,
                revoked_at timestamptz null,
                constraint ck_approval_delegation_dates check (to_date >= from_date),
                constraint ck_approval_delegation_not_self check (delegator_id <> delegate_id)
            );
            """);
        migrationBuilder.Sql("create index ix_approval_delegation_delegate on approval_delegation (delegate_id, from_date, to_date);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop table if exists approval_delegation;");
        migrationBuilder.Sql("drop table if exists project_approver;");
    }
}
