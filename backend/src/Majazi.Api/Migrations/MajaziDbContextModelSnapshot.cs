using Majazi.Api.Data;
using Majazi.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
public sealed class MajaziDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.0");

        modelBuilder.Entity("Majazi.Api.Models.AppUser", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Email").IsRequired().HasMaxLength(320).HasColumnType("character varying(320)");
            b.Property<bool>("IsActive").HasColumnType("boolean");
            b.Property<string>("PasswordHash").IsRequired().HasMaxLength(512).HasColumnType("character varying(512)");
            b.HasKey("Id");
            b.HasIndex("Email").IsUnique();
            b.ToTable("app_users", (string)null);
        });

        modelBuilder.Entity("Majazi.Api.Models.MarketingCampaign", b =>
        {
            b.HasOne("Majazi.Api.Models.Brand", "Brand").WithMany().HasForeignKey("BrandId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne("Majazi.Api.Models.Organization", "Organization").WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.Navigation("Brand"); b.Navigation("Organization");
        });

        modelBuilder.Entity("Majazi.Api.Models.OrganizationMembership", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<string>("Role").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<Guid>("UserId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("OrganizationId");
            b.HasIndex("UserId", "OrganizationId").IsUnique();
            b.ToTable("organization_memberships", (string)null);
        });

        modelBuilder.Entity("Majazi.Api.Models.Brand", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Name").IsRequired().HasMaxLength(180).HasColumnType("character varying(180)");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<string>("Slug").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.HasKey("Id");
            b.HasIndex("OrganizationId", "Slug").IsUnique();
            b.ToTable("brands", (string)null);
        });

        modelBuilder.Entity("Majazi.Api.Models.Organization", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Name").IsRequired().HasMaxLength(180).HasColumnType("character varying(180)");
            b.Property<string>("Slug").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.HasKey("Id");
            b.HasIndex("Slug").IsUnique();
            b.ToTable("organizations", (string)null);
        });


        modelBuilder.Entity("Majazi.Api.Models.CrmContact", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<Guid?>("BrandId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Email").HasMaxLength(320).HasColumnType("character varying(320)");
            b.Property<string>("FullName").IsRequired().HasMaxLength(180).HasColumnType("character varying(180)");
            b.Property<string>("Company").HasMaxLength(180).HasColumnType("character varying(180)");
            b.Property<string>("Notes").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<string>("Phone").HasMaxLength(40).HasColumnType("character varying(40)");
            b.HasKey("Id");
            b.HasIndex("BrandId");
            b.HasIndex("OrganizationId", "CreatedAt");
            b.ToTable("crm_contacts", (string)null);
        });

        modelBuilder.Entity("Majazi.Api.Models.CrmLead", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<Guid?>("BrandId").HasColumnType("uuid");
            b.Property<Guid?>("ContactId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<decimal?>("EstimatedValue").HasPrecision(18, 2).HasColumnType("numeric(18,2)");
            b.Property<string>("Notes").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<string>("Source").HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<string>("Stage").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("Title").IsRequired().HasMaxLength(240).HasColumnType("character varying(240)");
            b.HasKey("Id");
            b.HasIndex("BrandId");
            b.HasIndex("ContactId");
            b.HasIndex("OrganizationId", "Stage", "CreatedAt");
            b.ToTable("crm_leads", (string)null);
        });

        modelBuilder.Entity("Majazi.Api.Models.MarketingCampaign", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<Guid?>("BrandId").HasColumnType("uuid");
            b.Property<string>("Channel").IsRequired().HasMaxLength(40).HasColumnType("character varying(40)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<decimal>("Budget").HasPrecision(18, 2).HasColumnType("numeric(18,2)");
            b.Property<decimal>("ActualSpend").HasPrecision(18, 2).HasColumnType("numeric(18,2)");
            b.Property<decimal>("AttributedRevenue").HasPrecision(18, 2).HasColumnType("numeric(18,2)");
            b.Property<long>("Impressions").HasColumnType("bigint");
            b.Property<long>("Clicks").HasColumnType("bigint");
            b.Property<long>("Conversions").HasColumnType("bigint");
            b.Property<DateTimeOffset?>("EndsAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("Notes").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("Objective").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("StartsAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Status").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("BrandId");
            b.HasIndex("OrganizationId", "Status", "StartsAt");
            b.ToTable("marketing_campaigns", (string)null);
        });

        modelBuilder.Entity("Majazi.Api.Models.CrmActivity", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<string>("ActivityType").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<Guid?>("ContactId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("CreatedByUserId").HasColumnType("uuid");
            b.Property<Guid?>("LeadId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("OccurredAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<string>("Summary").IsRequired().HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.HasKey("Id");
            b.HasIndex("ContactId");
            b.HasIndex("LeadId");
            b.HasIndex("OrganizationId", "OccurredAt");
            b.ToTable("crm_activities", (string)null);
        });

        modelBuilder.Entity("Majazi.Api.Models.CrmContact", b =>
        {
            b.HasOne("Majazi.Api.Models.Brand", "Brand").WithMany().HasForeignKey("BrandId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne("Majazi.Api.Models.Organization", "Organization").WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.Navigation("Brand"); b.Navigation("Organization");
        });

        modelBuilder.Entity("Majazi.Api.Models.CrmLead", b =>
        {
            b.HasOne("Majazi.Api.Models.Brand", "Brand").WithMany().HasForeignKey("BrandId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne("Majazi.Api.Models.CrmContact", "Contact").WithMany().HasForeignKey("ContactId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne("Majazi.Api.Models.Organization", "Organization").WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.Navigation("Brand"); b.Navigation("Contact"); b.Navigation("Organization");
        });

        modelBuilder.Entity("Majazi.Api.Models.CrmActivity", b =>
        {
            b.HasOne("Majazi.Api.Models.CrmContact", "Contact").WithMany().HasForeignKey("ContactId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne("Majazi.Api.Models.CrmLead", "Lead").WithMany().HasForeignKey("LeadId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne("Majazi.Api.Models.Organization", "Organization").WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.Navigation("Contact"); b.Navigation("Lead"); b.Navigation("Organization");
        });

        modelBuilder.Entity("Majazi.Api.Models.OrganizationMembership", b =>
        {
            b.HasOne("Majazi.Api.Models.Organization", "Organization")
                .WithMany("Memberships")
                .HasForeignKey("OrganizationId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.HasOne("Majazi.Api.Models.AppUser", "User")
                .WithMany()
                .HasForeignKey("UserId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.Navigation("Organization");
            b.Navigation("User");
        });

        modelBuilder.Entity("Majazi.Api.Models.Brand", b =>
        {
            b.HasOne("Majazi.Api.Models.Organization", "Organization")
                .WithMany("Brands")
                .HasForeignKey("OrganizationId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.Navigation("Organization");
        });

        modelBuilder.Entity("Majazi.Api.Models.Organization", b =>
        {
            b.Navigation("Brands");
            b.Navigation("Memberships");
        });
    }
}
