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
#pragma warning restore 612, 618
    }
}
