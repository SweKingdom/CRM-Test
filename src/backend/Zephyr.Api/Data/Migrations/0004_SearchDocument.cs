using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zephyr.Api.Data.Migrations;

/// <summary>
/// search_document (US-147): one row per searchable record, kept by database triggers in the same transaction as the change.
/// A later story that adds a searchable table installs its trigger with one call of <c>search_install_trigger</c> in its own migration.
/// </summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0004_SearchDocument")]
public class SearchDocument : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // unaccent() is STABLE, so index and generated-column use needs an IMMUTABLE wrapper.
        migrationBuilder.Sql(
            """
            create function f_unaccent(text) returns text language sql immutable parallel safe strict as $$
                select public.unaccent('public.unaccent', $1)
            $$;
            """);

        // The one normalisation for stored text, titles and queries: lower case, no accents, punctuation as single spaces.
        migrationBuilder.Sql(
            """
            create function f_search_norm(text) returns text language sql immutable parallel safe strict as $$
                select trim(regexp_replace(lower(f_unaccent($1)), '[[:punct:][:space:]]+', ' ', 'g'))
            $$;
            """);

        // owner_id, account_id, country_code and business_unit are the contract of the permission predicate (US-144).
        migrationBuilder.Sql(
            """
            create table search_document (
                entity_type text not null,
                entity_id uuid not null,
                title text not null,
                subtitle text null,
                text text not null,
                tsv tsvector generated always as (to_tsvector('simple', text)) stored,
                owner_id uuid null,
                account_id uuid null,
                country_code text null,
                business_unit text null,
                updated_at timestamptz not null,
                constraint pk_search_document primary key (entity_type, entity_id),
                constraint ck_search_document_entity_type check (entity_type in ('account', 'contact', 'lead', 'opportunity', 'project'))
            );
            """);
        migrationBuilder.Sql("create index ix_search_document_tsv on search_document using gin (tsv);");
        migrationBuilder.Sql("create index ix_search_document_text_trgm on search_document using gin (text gin_trgm_ops);");

        // p_text holds the searchable fields besides the title (email, phone, customer number, city...); the title is added here.
        migrationBuilder.Sql(
            """
            create function search_upsert(
                p_type text, p_id uuid, p_title text, p_subtitle text, p_text text,
                p_owner_id uuid, p_account_id uuid, p_country_code text, p_business_unit text, p_updated_at timestamptz)
            returns void language sql as $$
                insert into search_document
                    (entity_type, entity_id, title, subtitle, text, owner_id, account_id, country_code, business_unit, updated_at)
                values
                    (p_type, p_id, p_title, p_subtitle, f_search_norm(concat_ws(' ', p_title, p_text)),
                     p_owner_id, p_account_id, p_country_code, p_business_unit, p_updated_at)
                on conflict (entity_type, entity_id) do update set
                    title = excluded.title, subtitle = excluded.subtitle, text = excluded.text,
                    owner_id = excluded.owner_id, account_id = excluded.account_id, country_code = excluded.country_code,
                    business_unit = excluded.business_unit, updated_at = excluded.updated_at
            $$;
            """);

        // Installs the sync trigger of one table. Every p_* expression is SQL over the row as NEW (for example 'new.name'),
        // 'null' for none. p_hidden is true when the record must not be searchable: deleted, and for contact and lead also
        // anonymised ('new.deleted_at is not null or new.anonymised_at is not null'). Existing rows are indexed at once.
        migrationBuilder.Sql(
            """
            create function search_install_trigger(
                p_table text, p_type text, p_title text, p_text text,
                p_subtitle text default 'null', p_owner_id text default 'null', p_account_id text default 'null',
                p_country_code text default 'null', p_business_unit text default 'null',
                p_hidden text default 'new.deleted_at is not null')
            returns void language plpgsql as $fn$
            begin
                execute format(
                    $body$
                    create or replace function %1$I() returns trigger language plpgsql as $t$
                    begin
                        if tg_op = 'DELETE' then
                            delete from search_document where entity_type = %2$L and entity_id = old.id;
                            return old;
                        elsif %3$s then
                            delete from search_document where entity_type = %2$L and entity_id = new.id;
                        else
                            perform search_upsert(%2$L, new.id, %4$s, %5$s, %6$s, %7$s, %8$s, %9$s, %10$s, new.updated_at);
                        end if;
                        return new;
                    end
                    $t$
                    $body$,
                    'search_sync_' || p_table, p_type, p_hidden, p_title, p_subtitle, p_text, p_owner_id, p_account_id,
                    p_country_code, p_business_unit);
                execute format('drop trigger if exists %I on %I', 'trg_search_' || p_table, p_table);
                execute format(
                    'create trigger %I after insert or update or delete on %I for each row execute function %I()',
                    'trg_search_' || p_table, p_table, 'search_sync_' || p_table);
                execute format('update %I set updated_at = updated_at where deleted_at is null', p_table);
            end
            $fn$;
            """);

        // Accounts have no owner; their country is the only scope column, so an account without a country is for org-wide roles only.
        migrationBuilder.Sql(
            "select search_install_trigger('account', 'account', 'new.name', 'new.country_code', " +
            "p_account_id => 'new.id', p_country_code => 'new.country_code');");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop trigger if exists trg_search_account on account;");
        migrationBuilder.Sql("drop function if exists search_sync_account();");
        migrationBuilder.Sql("drop function if exists search_install_trigger(text, text, text, text, text, text, text, text, text, text);");
        migrationBuilder.Sql(
            "drop function if exists search_upsert(text, uuid, text, text, text, uuid, uuid, text, text, timestamptz);");
        migrationBuilder.Sql("drop table if exists search_document;");
        migrationBuilder.Sql("drop function if exists f_search_norm(text);");
        migrationBuilder.Sql("drop function if exists f_unaccent(text);");
    }
}
