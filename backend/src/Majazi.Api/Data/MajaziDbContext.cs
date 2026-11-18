using Majazi.Api.Models;
using Microsoft.EntityFrameworkCore;
namespace Majazi.Api.Data;

public sealed class MajaziDbContext(DbContextOptions<MajaziDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Brand> Brands => Set<Brand>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>(e => { e.ToTable("organizations"); e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(180).IsRequired(); e.Property(x => x.Slug).HasMaxLength(100).IsRequired(); e.HasIndex(x => x.Slug).IsUnique(); });
        modelBuilder.Entity<Brand>(e => { e.ToTable("brands"); e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(180).IsRequired(); e.Property(x => x.Slug).HasMaxLength(100).IsRequired(); e.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique(); e.HasOne(x => x.Organization).WithMany(x => x.Brands).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade); });
    }
}
