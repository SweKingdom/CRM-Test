using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zephyr.Api.Data.Migrations;

/// <summary>Reference tables and the minimal account table (US-130). Rows come from Seed/*.sql.</summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0002_ReferenceData")]
public class ReferenceData : Migration
{
    private const string Conventions =
        "id uuid primary key default gen_random_uuid(), " +
        "created_at timestamptz not null default now(), updated_at timestamptz not null default now(), " +
        "created_by uuid null, updated_by uuid null, deleted_at timestamptz null, " +
        "custom jsonb not null default '{}'::jsonb";

    private const string ReferenceColumns =
        "key text not null unique, label text not null, sort_order int not null, active boolean not null default true";

    private static readonly (string Table, string Extra)[] ReferenceTables =
    {
        ("stage", "is_terminal boolean not null default false, exit_criteria jsonb null"),
        ("close_outcome", "maps_to_stage text null"),
        ("close_reason", "outcome_key text not null references close_outcome (key)"),
        ("call_outcome", "counts_as_contact boolean not null default false"),
        ("time_type",
            "billable_default boolean not null default false, requires_project boolean not null default true, " +
            "allows_future boolean not null default false, " +
            "category text not null check (category in ('client', 'internal', 'absence'))"),
        ("country", "iso2 char(2) not null unique"),
        ("currency", "iso3 char(3) not null unique, decimals int not null default 2"),
        ("meddpicc_element", ""),
    };

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var (table, extra) in ReferenceTables)
        {
            var more = extra == "" ? "" : ", " + extra;
            migrationBuilder.Sql($"create table {table} ({Conventions}, {ReferenceColumns}{more});");
        }

        migrationBuilder.Sql(
            $"create table account ({Conventions}, name text not null, country_code char(2) null);");
        migrationBuilder.Sql("create index ix_account_name_lower on account (lower(name));");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop table if exists account;");
        foreach (var (table, _) in ReferenceTables.Reverse())
        {
            migrationBuilder.Sql($"drop table if exists {table};");
        }
    }
}
