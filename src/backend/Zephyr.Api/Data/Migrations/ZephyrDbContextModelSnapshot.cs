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

        modelBuilder.Entity("Zephyr.Api.Data.TimeEntry", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid")
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            b.Property<DateTime?>("ApprovedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("approved_at");

            b.Property<Guid?>("ApprovedBy")
                .HasColumnType("uuid")
                .HasColumnName("approved_by");

            b.Property<bool>("Billable")
                .HasColumnType("boolean")
                .HasColumnName("billable");

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

            b.Property<int>("DurationMinutes")
                .HasColumnType("integer")
                .HasColumnName("duration_minutes");

            b.Property<string>("DynamicsId")
                .HasColumnType("text")
                .HasColumnName("dynamics_id");

            b.Property<string>("IsoWeek")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("iso_week");

            b.Property<string>("Notes")
                .HasMaxLength(500)
                .HasColumnType("character varying(500)")
                .HasColumnName("notes");

            b.Property<Guid?>("ProjectId")
                .HasColumnType("uuid")
                .HasColumnName("project_id");

            b.Property<string>("RejectionComment")
                .HasColumnType("text")
                .HasColumnName("rejection_comment");

            b.Property<string>("Source")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("source");

            b.Property<string>("Status")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("status");

            b.Property<DateTime?>("SubmittedAt")
                .HasColumnType("timestamp with time zone")
                .HasColumnName("submitted_at");

            b.Property<string>("TimeTypeKey")
                .IsRequired()
                .HasColumnType("text")
                .HasColumnName("time_type_key");

            b.Property<DateTime>("UpdatedAt")
                .IsConcurrencyToken()
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");

            b.Property<Guid?>("UpdatedBy")
                .HasColumnType("uuid")
                .HasColumnName("updated_by");

            b.Property<Guid>("UserId")
                .HasColumnType("uuid")
                .HasColumnName("user_id");

            b.Property<DateOnly>("WorkDate")
                .HasColumnType("date")
                .HasColumnName("work_date");

            b.HasKey("Id")
                .HasName("pk_time_entry");

            b.HasIndex("DynamicsId")
                .IsUnique()
                .HasDatabaseName("ux_time_entry_dynamics_id")
                .HasFilter("dynamics_id is not null");

            b.HasIndex("ProjectId", "WorkDate")
                .HasDatabaseName("ix_time_entry_project_id_work_date");

            b.HasIndex("UserId", "WorkDate")
                .HasDatabaseName("ix_time_entry_user_id_work_date");

            b.HasIndex("UserId", "IsoWeek", "Status")
                .HasDatabaseName("ix_time_entry_user_id_iso_week_status");

            b.ToTable("time_entry", null, t =>
            {
                t.HasCheckConstraint("ck_time_entry_duration", "duration_minutes > 0 and duration_minutes % 15 = 0");

                t.HasCheckConstraint("ck_time_entry_iso_week", "iso_week ~ '^[0-9]{4}-W[0-9]{2}$'");

                t.HasCheckConstraint("ck_time_entry_source", "source in ('ui', 'api', 'mcp', 'import', 'form', 'webhook', 'automation')");

                t.HasCheckConstraint("ck_time_entry_status", "status in ('Draft', 'Submitted', 'Approved', 'Rejected')");
            });
        });
#pragma warning restore 612, 618
    }
}
