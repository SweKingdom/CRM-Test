using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zephyr.Api.Data.Migrations;

[DbContext(typeof(ZephyrDbContext))]
partial class ZephyrDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "8.0.11")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("Zephyr.Api.Data.Account", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid")
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            b.Property<DateTime>("CreatedAt")
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");

            b.Property<Guid?>("CreatedBy")
                .HasColumnType("uuid")
                .HasColumnName("created_by");

            b.Property<string>("CountryCode")
                .HasColumnType("character(2)")
                .HasColumnName("country_code");

            b.Property<string>("Custom")
                .IsRequired()
                .ValueGeneratedOnAdd()
                .HasColumnType("jsonb")
                .HasColumnName("custom")
                .HasDefaultValueSql("'{}'::jsonb");

            b.Property<DateTime?>("DeletedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("deleted_at");

            b.Property<string>("Name")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("name");

            b.Property<DateTime>("UpdatedAt")
                .IsConcurrencyToken()
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");

            b.Property<Guid?>("UpdatedBy")
                .HasColumnType("uuid")
                .HasColumnName("updated_by");

            b.HasKey("Id")
                .HasName("account_pkey");

            b.ToTable("account", (string)null);
        });

        modelBuilder.Entity("Zephyr.Api.Data.AppUser", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid")
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            b.Property<bool>("Active")
                .HasColumnType("boolean")
                .HasColumnName("active");

            b.Property<DateTime>("CreatedAt")
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");

            b.Property<Guid?>("CreatedBy")
                .HasColumnType("uuid")
                .HasColumnName("created_by");

            b.Property<string>("Custom")
                .IsRequired()
                .ValueGeneratedOnAdd()
                .HasColumnType("jsonb")
                .HasColumnName("custom")
                .HasDefaultValueSql("'{}'::jsonb");

            b.Property<DateTime?>("DeletedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("deleted_at");

            b.Property<string>("Email")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("email");

            b.Property<string>("HomeCountry")
                .HasColumnType("character(2)")
                .HasColumnName("home_country");

            b.Property<string>("Issuer")
                .HasColumnType("text")
                .HasColumnName("issuer");

            b.Property<DateTime?>("LastLoginAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("last_login_at");

            b.Property<Guid?>("ManagerId")
                .HasColumnType("uuid")
                .HasColumnName("manager_id");

            b.Property<string>("Name")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("name");

            b.Property<string[]>("Roles")
                .IsRequired()
                .HasColumnType("text[]")
                .HasColumnName("roles");

            b.Property<string>("Subject")
                .HasColumnType("text")
                .HasColumnName("subject");

            b.Property<DateTime>("UpdatedAt")
                .IsConcurrencyToken()
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");

            b.Property<Guid?>("UpdatedBy")
                .HasColumnType("uuid")
                .HasColumnName("updated_by");

            b.HasKey("Id")
                .HasName("pk_app_user");

            b.HasIndex("Issuer", "Subject")
                .IsUnique()
                .HasDatabaseName("ux_app_user_issuer_subject")
                .HasFilter("issuer is not null and subject is not null");

            b.ToTable("app_user", (string)null);
        });

        modelBuilder.Entity("Zephyr.Api.Data.AuditLog", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid")
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            b.Property<string>("Action")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("action");

            b.Property<DateTime>("At")
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("at")
                .HasDefaultValueSql("now()");

            b.Property<string>("Detail")
                .HasColumnType("jsonb")
                .HasColumnName("detail");

            b.Property<Guid>("EntityId")
                .HasColumnType("uuid")
                .HasColumnName("entity_id");

            b.Property<string>("EntityType")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("entity_type");

            b.Property<Guid?>("UserId")
                .HasColumnType("uuid")
                .HasColumnName("user_id");

            b.HasKey("Id")
                .HasName("pk_audit_log");

            b.HasIndex("EntityType", "EntityId", "At")
                .HasDatabaseName("ix_audit_log_entity_type_entity_id_at");

            b.ToTable("audit_log", (string)null);
        });

        modelBuilder.Entity("Zephyr.Api.Data.ExchangeRate", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid")
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            b.Property<DateTime>("CreatedAt")
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");

            b.Property<Guid?>("CreatedBy")
                .HasColumnType("uuid")
                .HasColumnName("created_by");

            b.Property<string>("Currency")
                .IsRequired()
                .HasColumnType("char(3)")
                .HasColumnName("currency");

            b.Property<string>("Custom")
                .IsRequired()
                .ValueGeneratedOnAdd()
                .HasColumnType("jsonb")
                .HasColumnName("custom")
                .HasDefaultValueSql("'{}'::jsonb");

            b.Property<DateTime?>("DeletedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("deleted_at");

            b.Property<DateOnly>("Month")
                .HasColumnType("date")
                .HasColumnName("month");

            b.Property<decimal>("RateToSek")
                .HasColumnType("numeric(18,6)")
                .HasColumnName("rate_to_sek");

            b.Property<DateTime>("UpdatedAt")
                .IsConcurrencyToken()
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");

            b.Property<Guid?>("UpdatedBy")
                .HasColumnType("uuid")
                .HasColumnName("updated_by");

            b.HasKey("Id")
                .HasName("pk_exchange_rate");

            b.HasIndex("Currency", "Month")
                .IsUnique()
                .HasDatabaseName("ux_exchange_rate_currency_month");

            b.ToTable("exchange_rate", null, t =>
            {
                t.HasCheckConstraint("ck_exchange_rate_currency", "currency <> 'SEK'");

                t.HasCheckConstraint("ck_exchange_rate_month", "date_trunc('month', month::timestamp) = month::timestamp");

                t.HasCheckConstraint("ck_exchange_rate_rate", "rate_to_sek > 0");
            });
        });

        modelBuilder.Entity("Zephyr.Api.Data.ExternalId", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid")
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            b.Property<DateTime>("CreatedAt")
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");

            b.Property<Guid?>("CreatedBy")
                .HasColumnType("uuid")
                .HasColumnName("created_by");

            b.Property<string>("Custom")
                .IsRequired()
                .ValueGeneratedOnAdd()
                .HasColumnType("jsonb")
                .HasColumnName("custom")
                .HasDefaultValueSql("'{}'::jsonb");

            b.Property<DateTime?>("DeletedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("deleted_at");

            b.Property<Guid>("EntityId")
                .HasColumnType("uuid")
                .HasColumnName("entity_id");

            b.Property<string>("EntityType")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("entity_type");

            b.Property<string>("System")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("system");

            b.Property<DateTime>("UpdatedAt")
                .IsConcurrencyToken()
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");

            b.Property<Guid?>("UpdatedBy")
                .HasColumnType("uuid")
                .HasColumnName("updated_by");

            b.Property<string>("Value")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("value");

            b.HasKey("Id")
                .HasName("pk_external_id");

            b.HasIndex("EntityType", "EntityId")
                .HasDatabaseName("ix_external_id_entity_type_entity_id");

            b.HasIndex("System", "EntityType", "Value")
                .IsUnique()
                .HasDatabaseName("ux_external_id_system_entity_type_value");

            b.ToTable("external_id", null, t =>
            {
                t.HasCheckConstraint("ck_external_id_system", "\"system\" IN ('dynamics', 'fortnox')");
            });
        });

        modelBuilder.Entity("Zephyr.Api.Data.Lead", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid")
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            b.Property<Guid?>("AccountId")
                .HasColumnType("uuid")
                .HasColumnName("account_id");

            b.Property<int>("CallTries")
                .HasColumnType("integer")
                .HasColumnName("call_tries");

            b.Property<string>("CompanyName")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("company_name");

            b.Property<Guid?>("ConvertedContactId")
                .HasColumnType("uuid")
                .HasColumnName("converted_contact_id");

            b.Property<Guid?>("ConvertedOpportunityId")
                .HasColumnType("uuid")
                .HasColumnName("converted_opportunity_id");

            b.Property<string>("CountryCode")
                .HasColumnType("character(2)")
                .HasColumnName("country_code");

            b.Property<DateTime>("CreatedAt")
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");

            b.Property<Guid?>("CreatedBy")
                .HasColumnType("uuid")
                .HasColumnName("created_by");

            b.Property<string>("Custom")
                .IsRequired()
                .ValueGeneratedOnAdd()
                .HasColumnType("jsonb")
                .HasColumnName("custom")
                .HasDefaultValueSql("'{}'::jsonb");

            b.Property<DateTime?>("DeletedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("deleted_at");

            b.Property<string>("DisqualifyReason")
                .HasColumnType("text")
                .HasColumnName("disqualify_reason_key");

            b.Property<string>("Email")
                .HasColumnType("text")
                .HasColumnName("email");

            b.Property<string>("FirstName")
                .HasColumnType("text")
                .HasColumnName("first_name");

            b.Property<string>("JobTitle")
                .HasColumnType("text")
                .HasColumnName("job_title");

            b.Property<string>("LastCallOutcome")
                .HasColumnType("text")
                .HasColumnName("last_call_outcome_key");

            b.Property<string>("LastName")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("last_name");

            b.Property<string>("LeadSource")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("lead_source_key");

            b.Property<string>("Mobile")
                .HasColumnType("text")
                .HasColumnName("mobile");

            b.Property<string>("NextStep")
                .HasColumnType("text")
                .HasColumnName("next_step");

            b.Property<DateTime?>("NextStepDate")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("next_step_date");

            b.Property<Guid>("OwnerUserId")
                .HasColumnType("uuid")
                .HasColumnName("owner_user_id");

            b.Property<string>("Phone")
                .HasColumnType("text")
                .HasColumnName("phone");

            b.Property<string>("Status")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("status");

            b.Property<DateTime>("UpdatedAt")
                .IsConcurrencyToken()
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");

            b.Property<Guid?>("UpdatedBy")
                .HasColumnType("uuid")
                .HasColumnName("updated_by");

            b.Property<string>("Website")
                .HasColumnType("text")
                .HasColumnName("website");

            b.HasKey("Id")
                .HasName("lead_pkey");

            b.HasIndex("LeadSource")
                .HasDatabaseName("ix_lead_lead_source_key")
                .HasFilter("deleted_at is null");

            b.HasIndex("OwnerUserId", "Status", "NextStepDate")
                .HasDatabaseName("ix_lead_owner_user_id_status_next_step_date")
                .HasFilter("deleted_at is null");

            b.HasIndex("UpdatedAt", "Id")
                .HasDatabaseName("ix_lead_updated_at_id")
                .HasFilter("deleted_at is null");

            b.ToTable("lead", null, t =>
            {
                t.HasCheckConstraint("ck_lead_call_tries", "call_tries >= 0");

                t.HasCheckConstraint("ck_lead_status", "status in ('open', 'converted', 'disqualified')");
            });
        });
#pragma warning restore 612, 618
    }
}
