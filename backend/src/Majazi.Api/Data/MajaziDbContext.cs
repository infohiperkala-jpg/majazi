using Majazi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Majazi.Api.Data;

public sealed class MajaziDbContext(DbContextOptions<MajaziDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();

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
    }
}
