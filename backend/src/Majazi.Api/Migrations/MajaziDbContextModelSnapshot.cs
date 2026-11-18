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
        });
    }
}
