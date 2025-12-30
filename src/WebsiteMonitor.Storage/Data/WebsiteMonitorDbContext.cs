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
    public DbSet<WebsiteMonitor.Storage.Models.Target> Targets => Set<WebsiteMonitor.Storage.Models.Target>();
    public DbSet<WebsiteMonitor.Storage.Models.Check> Checks => Set<WebsiteMonitor.Storage.Models.Check>();
    public DbSet<WebsiteMonitor.Storage.Models.TargetState> States => Set<WebsiteMonitor.Storage.Models.TargetState>();
    public DbSet<Event> Events => Set<Event>();
	public DbSet<SmtpSettings> SmtpSettings => Set<SmtpSettings>();
	public DbSet<Recipient> Recipients => Set<Recipient>();
	public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();

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

		modelBuilder.Entity<SmtpSettings>(b =>
		{
			b.HasKey(x => x.InstanceId);
			b.Property(x => x.Host).HasMaxLength(255);
			b.Property(x => x.Username).HasMaxLength(255);
			b.Property(x => x.FromAddress).HasMaxLength(255);
			b.Property(x => x.PasswordProtected);
			b.Property(x => x.UpdatedUtc);
		});

		modelBuilder.Entity<Recipient>(b =>
		{
			b.HasKey(x => x.RecipientId);
			b.Property(x => x.InstanceId).HasMaxLength(64);
			b.Property(x => x.Email).HasMaxLength(255);
			b.HasIndex(x => new { x.InstanceId, x.Email }).IsUnique();
		});

		modelBuilder.Entity<WebhookEndpoint>(b =>
		{
			b.ToTable("WebhookEndpoints");
			b.HasKey(x => x.WebhookEndpointId);
			b.Property(x => x.InstanceId).HasMaxLength(64).IsRequired();
			b.Property(x => x.Url).HasMaxLength(2048).IsRequired();
			b.HasIndex(x => new { x.InstanceId, x.Url }).IsUnique();
		});

        modelBuilder.Entity<WebsiteMonitor.Storage.Models.Target>(b =>
        {
            b.ToTable("Targets");
            b.HasKey(x => x.TargetId);
            b.Property(x => x.InstanceId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Url).HasMaxLength(2048).IsRequired();
            b.Property(x => x.LoginRule).HasMaxLength(200);
            b.HasIndex(x => new { x.InstanceId, x.Url }).IsUnique(false);
        });

        modelBuilder.Entity<WebsiteMonitor.Storage.Models.Check>(b =>
        {
            b.ToTable("Checks");
            b.HasKey(x => x.CheckId);
            b.Property(x => x.Summary).HasMaxLength(4000);
            b.HasIndex(x => new { x.TargetId, x.TimestampUtc });
        });

        modelBuilder.Entity<WebsiteMonitor.Storage.Models.TargetState>(b =>
        {
            b.ToTable("State");
            b.HasKey(x => x.TargetId);
            b.Property(x => x.LastSummary).HasMaxLength(4000);
        });
    }
}
