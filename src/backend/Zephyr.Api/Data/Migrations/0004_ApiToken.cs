using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zephyr.Api.Data.Migrations;

/// <summary>api_token: personal API tokens (US-142). Holds only the hash and the hash algorithm name, never the plaintext.</summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0004_ApiToken")]
public class AddApiToken : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            create table api_token (
                id uuid constraint pk_api_token primary key default gen_random_uuid(),
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                created_by uuid null,
                updated_by uuid null,
                deleted_at timestamptz null,
                custom jsonb not null default '{}'::jsonb,
                user_id uuid not null constraint fk_api_token_user_id references app_user (id),
                name text not null constraint ck_api_token_name check (char_length(name) between 1 and 100),
                token_hash char(64) not null,
                hash_algorithm text not null default 'sha256',
                prefix varchar(12) not null,
                expires_at timestamptz not null,
                revoked_at timestamptz null,
                last_used_at timestamptz null
            );
            """);
        migrationBuilder.Sql("create unique index ux_api_token_token_hash on api_token (token_hash);");
        migrationBuilder.Sql("create index ix_api_token_user_id on api_token (user_id) where revoked_at is null;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop table if exists api_token;");
    }
}
