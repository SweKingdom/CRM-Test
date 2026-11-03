using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zephyr.Api.Data.Migrations;

/// <summary>app_user: who can sign in (US-140). Pre-provisioned; no password or secret column.</summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0003_AppUser")]
public class AddAppUser : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            create table app_user (
                id uuid constraint pk_app_user primary key default gen_random_uuid(),
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                created_by uuid null,
                updated_by uuid null,
                deleted_at timestamptz null,
                custom jsonb not null default '{}'::jsonb,
                name text not null,
                email text not null,
                issuer text null,
                subject text null,
                home_country char(2) null references country (iso2),
                roles text[] not null default '{}',
                active boolean not null default true,
                last_login_at timestamptz null
            );
            """);
        migrationBuilder.Sql(
            "create unique index ux_app_user_email_lower on app_user (lower(email)) where deleted_at is null;");
        migrationBuilder.Sql(
            "create unique index ux_app_user_issuer_subject on app_user (issuer, subject) where issuer is not null and subject is not null;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop table if exists app_user;");
    }
}
