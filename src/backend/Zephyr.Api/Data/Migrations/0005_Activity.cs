using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zephyr.Api.Data.Migrations;

/// <summary>
/// One <c>activity</c> table for calls, meetings, notes, tasks and emails, and its participants (US-151, ADR-010).
/// "Regarding" is polymorphic (type + id, no foreign key, validated by the API); <c>account_id</c> is derived by the server.
/// </summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0005_Activity")]
public class AddActivity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            $"""
            create table activity (
                {ReferenceData.Conventions},
                type text not null constraint ck_activity_type check (type in ('call', 'meeting', 'note', 'task', 'email')),
                subject text not null constraint ck_activity_subject check (char_length(subject) between 1 and 250),
                body text null,
                next_step text null,
                transcript text null,
                location text null,
                meeting_url text null,
                phone_number text null,
                regarding_type text null constraint ck_activity_regarding_type
                    check (regarding_type in ('lead', 'contact', 'account', 'opportunity', 'project', 'service_request')),
                regarding_id uuid null,
                account_id uuid null constraint fk_activity_account references account (id),
                direction text null constraint ck_activity_direction check (direction in ('inbound', 'outbound')),
                due timestamptz null,
                start_at timestamptz null,
                end_at timestamptz null,
                completed_at timestamptz null,
                duration_minutes int null constraint ck_activity_duration check (duration_minutes >= 0),
                call_outcome_key text null constraint fk_activity_call_outcome references call_outcome (key),
                connected boolean null,
                source text not null default 'ui' constraint ck_activity_source check (source in ('ui', 'mcp', 'import', 'api')),
                constraint ck_activity_regarding_both check ((regarding_type is null) = (regarding_id is null)),
                constraint ck_activity_end_after_start check (end_at is null or start_at is null or end_at >= start_at),
                constraint ck_activity_outcome_only_call check ((call_outcome_key is null and connected is null) or type = 'call')
            );
            """);
        // The account timeline: newest first, keyset paged on (coalesce(start_at, created_at), id).
        migrationBuilder.Sql(
            "create index ix_activity_account_timeline on activity (account_id, (coalesce(start_at, created_at)) desc, id desc) " +
            "where deleted_at is null;");
        migrationBuilder.Sql("create index ix_activity_regarding on activity (regarding_type, regarding_id);");

        migrationBuilder.Sql(
            $"""
            create table activity_participant (
                {ReferenceData.Conventions},
                activity_id uuid not null constraint fk_activity_participant_activity references activity (id) on delete cascade,
                role text not null constraint ck_activity_participant_role check (role in ('from', 'to', 'cc', 'attendee')),
                contact_id uuid null,
                user_id uuid null constraint fk_activity_participant_user references app_user (id),
                email text null,
                constraint ck_activity_participant_one_target check (num_nonnulls(contact_id, user_id, email) = 1)
            );
            """);
        migrationBuilder.Sql("create index ix_activity_participant_activity_id on activity_participant (activity_id);");
        migrationBuilder.Sql("create index ix_activity_participant_contact_id on activity_participant (contact_id);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop table if exists activity_participant;");
        migrationBuilder.Sql("drop table if exists activity;");
    }
}
