using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zephyr.Api.Data.Migrations;

/// <summary>
/// The lead table (US-153) and its <c>lead_source</c> picklist. The picklist has the shape of the other reference tables but no
/// seeded rows: the lead source values are not confirmed yet, so <c>Seed/*.sql</c> does not invent any.
/// </summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0006_AddLead")]
public class AddLead : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"create table lead_source ({ReferenceData.Conventions}, {ReferenceData.ReferenceColumns});");

        // country_code and the call, account and owner keys are foreign keys; disqualify_reason_key and the converted_* ids are
        // set by US-155, which adds their keys. call_tries and last_call_outcome_key are written by US-154 only.
        migrationBuilder.Sql(
            $"""
            create table lead (
                {ReferenceData.Conventions},
                first_name text null,
                last_name text not null,
                company_name text not null,
                job_title text null,
                phone text null,
                mobile text null,
                email text null,
                website text null,
                lead_source_key text not null constraint fk_lead_lead_source references lead_source (key),
                country_code char(2) null constraint fk_lead_country references country (iso2),
                call_tries int not null default 0 constraint ck_lead_call_tries check (call_tries >= 0),
                last_call_outcome_key text null constraint fk_lead_call_outcome references call_outcome (key),
                next_step text null,
                next_step_date timestamptz null,
                account_id uuid null constraint fk_lead_account references account (id),
                owner_user_id uuid not null constraint fk_lead_owner references app_user (id),
                status text not null default 'open' constraint ck_lead_status check (status in ('open', 'converted', 'disqualified')),
                disqualify_reason_key text null,
                converted_contact_id uuid null,
                converted_opportunity_id uuid null
            );
            """);
        migrationBuilder.Sql(
            "create index ix_lead_owner_user_id_status_next_step_date on lead (owner_user_id, status, next_step_date) where deleted_at is null;");
        migrationBuilder.Sql("create index ix_lead_lead_source_key on lead (lead_source_key) where deleted_at is null;");
        migrationBuilder.Sql("create index ix_lead_updated_at_id on lead (updated_at, id) where deleted_at is null;");
        migrationBuilder.Sql("create index ix_lead_account_id on lead (account_id) where account_id is not null;");

        // Search (US-147): the company is the title, the person the subtitle. Owner and country are what the read scopes compare with.
        migrationBuilder.Sql(
            "select search_install_trigger('lead', 'lead', 'new.company_name', " +
            "'concat_ws('' '', new.first_name, new.last_name, new.email, new.phone, new.mobile)', " +
            "p_subtitle => 'concat_ws('' '', new.first_name, new.last_name)', p_owner_id => 'new.owner_user_id', " +
            "p_account_id => 'new.account_id', p_country_code => 'new.country_code');");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop trigger if exists trg_search_lead on lead;");
        migrationBuilder.Sql("drop function if exists search_sync_lead();");
        migrationBuilder.Sql("delete from search_document where entity_type = 'lead';");
        migrationBuilder.Sql("drop table if exists lead;");
        migrationBuilder.Sql("drop table if exists lead_source;");
    }
}
