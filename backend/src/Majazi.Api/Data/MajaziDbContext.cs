using Majazi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Majazi.Api.Data;

public sealed class MajaziDbContext(DbContextOptions<MajaziDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<CrmContact> CrmContacts => Set<CrmContact>();
    public DbSet<CrmLead> CrmLeads => Set<CrmLead>();
    public DbSet<CrmActivity> CrmActivities => Set<CrmActivity>();
    public DbSet<MarketingCampaign> MarketingCampaigns => Set<MarketingCampaign>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>(e =>
        {
            e.ToTable("organizations"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(180).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
        });
        modelBuilder.Entity<Brand>(e =>
        {
            e.ToTable("brands"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(180).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique();
            e.HasOne(x => x.Organization).WithMany(x => x.Brands).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("app_users"); e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
        });
        modelBuilder.Entity<OrganizationMembership>(e =>
        {
            e.ToTable("organization_memberships"); e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.UserId, x.OrganizationId }).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Organization).WithMany(x => x.Memberships).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmContact>(e =>
        {
            e.ToTable("crm_contacts"); e.HasKey(x => x.Id);
            e.Property(x => x.FullName).HasMaxLength(180).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Company).HasMaxLength(180);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Brand).WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<CrmLead>(e =>
        {
            e.ToTable("crm_leads"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(240).IsRequired();
            e.Property(x => x.Stage).HasMaxLength(32).IsRequired();
            e.Property(x => x.EstimatedValue).HasPrecision(18, 2);
            e.Property(x => x.Source).HasMaxLength(120);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.HasIndex(x => new { x.OrganizationId, x.Stage, x.CreatedAt });
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Brand).WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<MarketingCampaign>(e =>
        {
            e.ToTable("marketing_campaigns"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Objective).HasMaxLength(1000);
            e.Property(x => x.Channel).HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.Budget).HasPrecision(18, 2);
            e.Property(x => x.ActualSpend).HasPrecision(18, 2);
            e.Property(x => x.AttributedRevenue).HasPrecision(18, 2);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.HasIndex(x => new { x.OrganizationId, x.Status, x.StartsAt });
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Brand).WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<CrmActivity>(e =>
        {
            e.ToTable("crm_activities"); e.HasKey(x => x.Id);
            e.Property(x => x.ActivityType).HasMaxLength(32).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
