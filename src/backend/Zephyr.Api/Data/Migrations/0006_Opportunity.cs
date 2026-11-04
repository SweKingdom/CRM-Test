using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zephyr.Api.Data.Migrations;

/// <summary>
/// The opportunity (US-156). The stage table exists since 0002; it gains <c>outcome</c> and a not-null <c>exit_criteria</c>
/// (default <c>{}</c>, shape owned by US-157). <c>lead_source</c> is a reference table that has no seed rows yet (no value list is
/// agreed). The opportunity keeps only <c>stage_key</c>: renaming a stage label never touches an opportunity row. The closed_*
/// and competitor columns are written by the close endpoint (US-158); the amount is stamped with SEK when the deal closes.
/// Opportunities are searchable like accounts (the trigger of US-147), scoped by owner and contracting country.
/// </summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0006_Opportunity")]
public class AddOpportunity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("alter table stage add column outcome text null;");
        migrationBuilder.Sql(
            "alter table stage add constraint ck_stage_outcome check " +
            "(outcome is null or (is_terminal and outcome in ('won', 'lost', 'no_decision', 'disqualified')));");
        migrationBuilder.Sql("update stage set exit_criteria = '{}'::jsonb where exit_criteria is null;");
        migrationBuilder.Sql("alter table stage alter column exit_criteria set default '{}'::jsonb, alter column exit_criteria set not null;");

        migrationBuilder.Sql(
            $"create table lead_source ({ReferenceData.Conventions}, " +
            "key text not null unique, label text not null, sort_order int not null, active boolean not null default true);");

        migrationBuilder.Sql(
            $"""
            create table opportunity (
                {ReferenceData.Conventions},
                name text not null,
                account_id uuid not null constraint fk_opportunity_account references account (id),
                stage_key text not null constraint fk_opportunity_stage references stage (key),
                amount numeric(18,2) null,
                currency char(3) null,
                amount_sek numeric(18,2) null,
                rate_date date null,
                hourly_rate numeric(18,2) null constraint ck_opportunity_hourly_rate check (hourly_rate >= 0),
                est_hours numeric(9,2) null constraint ck_opportunity_est_hours check (est_hours >= 0),
                contracting_country_key text null constraint fk_opportunity_country references country (key),
                next_step text null,
                next_step_date date null,
                lead_source_key text null constraint fk_opportunity_lead_source references lead_source (key),
                owner_user_id uuid not null constraint fk_opportunity_owner references app_user (id),
                forecast_category text null,
                expected_close_date date null,
                closed_outcome text null constraint fk_opportunity_closed_outcome references close_outcome (key),
                closed_reason_key text null constraint fk_opportunity_closed_reason references close_reason (key),
                competitor text null,
                closed_at timestamptz null,
                custom_platform text null,
                delivery_team text null,
                proposed_po text null,
                constraint ck_opportunity_forecast_category check (forecast_category in ('pipeline', 'best_case', 'commit', 'omitted')),
                constraint ck_opportunity_amount_currency check ((amount is null) = (currency is null))
            );
            """);
        migrationBuilder.Sql("create index ix_opportunity_owner_stage on opportunity (owner_user_id, stage_key) where deleted_at is null;");
        migrationBuilder.Sql("create index ix_opportunity_account_id on opportunity (account_id);");
        migrationBuilder.Sql("create index ix_opportunity_expected_close_date on opportunity (expected_close_date);");

        migrationBuilder.Sql(
            "select search_install_trigger('opportunity', 'opportunity', 'new.name', 'new.next_step', " +
            "p_owner_id => 'new.owner_user_id', p_account_id => 'new.account_id', p_country_code => 'new.contracting_country_key');");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop trigger if exists trg_search_opportunity on opportunity;");
        migrationBuilder.Sql("drop function if exists search_sync_opportunity();");
        migrationBuilder.Sql("delete from search_document where entity_type = 'opportunity';");
        migrationBuilder.Sql("drop table if exists opportunity;");
        migrationBuilder.Sql("drop table if exists lead_source;");
        migrationBuilder.Sql("alter table stage alter column exit_criteria drop not null, alter column exit_criteria drop default;");
        migrationBuilder.Sql("alter table stage drop constraint if exists ck_stage_outcome;");
        migrationBuilder.Sql("alter table stage drop column if exists outcome;");
    }
}
