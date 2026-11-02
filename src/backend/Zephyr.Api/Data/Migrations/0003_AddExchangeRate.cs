using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zephyr.Api.Data.Migrations;

/// <summary>Monthly exchange rates to SEK (US-134). Rows come from Seed/0003_exchange_rates.sql and from admins.</summary>
[DbContext(typeof(ZephyrDbContext))]
[Migration("0003_AddExchangeRate")]
public class AddExchangeRate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            $"create table exchange_rate ({ReferenceData.Conventions}, " +
            "currency char(3) not null references currency (iso3), " +
            "month date not null, " +
            "rate_to_sek numeric(18,6) not null, " +
            "constraint ck_exchange_rate_currency check (currency <> 'SEK'), " +
            "constraint ck_exchange_rate_month check (date_trunc('month', month::timestamp) = month::timestamp), " +
            "constraint ck_exchange_rate_rate check (rate_to_sek > 0));");
        migrationBuilder.Sql("create unique index ux_exchange_rate_currency_month on exchange_rate (currency, month);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop table if exists exchange_rate;");
    }
}
