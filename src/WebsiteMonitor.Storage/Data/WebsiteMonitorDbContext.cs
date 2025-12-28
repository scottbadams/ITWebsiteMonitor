using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WebsiteMonitor.Storage.Identity;
using WebsiteMonitor.Storage.Models;

namespace WebsiteMonitor.Storage.Data;

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using WebsiteMonitor.Storage.Identity;

public sealed class WebsiteMonitorDbContext : IdentityDbContext<ApplicationUser>
{
    public WebsiteMonitorDbContext(DbContextOptions<WebsiteMonitorDbContext> options) : base(options) { }

    public DbSet<Instance> Instances => Set<Instance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Instance>(b =>
        {
            b.ToTable("Instances");
            b.HasKey(x => x.InstanceId);
            b.Property(x => x.InstanceId).HasMaxLength(64);
            b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired();
            b.Property(x => x.CreatedUtc).IsRequired();
        });
    }
}
