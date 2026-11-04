using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zephyr.Api.Data.Migrations;

/// <summary>
/// The <c>time_entry</c> table (US-163). <c>time_type</c> and its 11 rows come from migration 0002 and <c>Seed/0001_reference_data.sql</c>.
/// <c>project_id</c> has no foreign key yet: the project register (US-161) adds it. The money columns stay null until approval stamps
/// them (US-167). The rules (future dates, daily limit, status) live in the API; the table keeps what a bad write must never store.
/// </summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0006_TimeEntry")]
public class AddTimeEntry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            create table time_entry (
                id uuid constraint pk_time_entry primary key default gen_random_uuid(),
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                created_by uuid null,
                updated_by uuid null,
                deleted_at timestamptz null,
                custom jsonb not null default '{}'::jsonb,
                user_id uuid not null constraint fk_time_entry_user references app_user (id),
                project_id uuid null,
                time_type_key text not null constraint fk_time_entry_time_type references time_type (key),
                work_date date not null,
                duration_minutes int not null,
                iso_week text not null,
                billable boolean not null,
                status text not null default 'Draft',
                submitted_at timestamptz null,
                approved_by uuid null constraint fk_time_entry_approved_by references app_user (id),
                approved_at timestamptz null,
                rejection_comment text null,
                notes varchar(500) null,
                source text not null,
                dynamics_id text null,
                hourly_rate numeric(18,2) null,
                amount numeric(18,2) null,
                currency char(3) null,
                amount_sek numeric(18,2) null,
                rate_date date null,
                constraint ck_time_entry_duration check (duration_minutes > 0 and duration_minutes % 15 = 0),
                constraint ck_time_entry_iso_week check (iso_week ~ '^[0-9]{4}-W[0-9]{2}$'),
                constraint ck_time_entry_status check (status in ('Draft', 'Submitted', 'Approved', 'Rejected')),
                constraint ck_time_entry_source check (source in ('ui', 'api', 'mcp', 'import', 'form', 'webhook', 'automation'))
            );
            """);
        migrationBuilder.Sql("create index ix_time_entry_user_id_work_date on time_entry (user_id, work_date);");
        migrationBuilder.Sql("create index ix_time_entry_project_id_work_date on time_entry (project_id, work_date);");
        migrationBuilder.Sql("create index ix_time_entry_user_id_iso_week_status on time_entry (user_id, iso_week, status);");
        migrationBuilder.Sql("create unique index ux_time_entry_dynamics_id on time_entry (dynamics_id) where dynamics_id is not null;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop table if exists time_entry;");
    }
}
