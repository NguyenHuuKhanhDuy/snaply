using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Snaply.Core.Models;

namespace Snaply.Infrastructure.Persistence;

public class SnaplyDbContext : DbContext
{
    public SnaplyDbContext(DbContextOptions<SnaplyDbContext> options) : base(options) { }

    public DbSet<Screenshot> Screenshots => Set<Screenshot>();
    public DbSet<AnnotationDocument> AnnotationDocuments => Set<AnnotationDocument>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ScreenshotTag> ScreenshotTags => Set<ScreenshotTag>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        b.Entity<Screenshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FilePath).IsRequired().HasMaxLength(1024);
            e.Property(x => x.ThumbnailPath).HasMaxLength(1024);
            e.Property(x => x.FileName).IsRequired().HasMaxLength(260);
            e.Property(x => x.Format).IsRequired().HasMaxLength(8);
            e.Property(x => x.CreatedAt).HasConversion(utc);
            e.Property(x => x.CreatedByCaptureMethod).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.FilePath).IsUnique();
            e.HasIndex(x => new { x.CreatedAt, x.Id });
            e.HasIndex(x => x.IsFavorite);
            e.HasIndex(x => x.FileName);
            e.HasOne(x => x.AnnotationDocument).WithOne(x => x.Screenshot!).HasForeignKey<AnnotationDocument>(x => x.ScreenshotId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AnnotationDocument>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DocumentData).IsRequired();
            e.Property(x => x.UpdatedAt).HasConversion(utc);
            e.HasIndex(x => x.ScreenshotId).IsUnique();
        });

        b.Entity<Tag>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(50).UseCollation("NOCASE");
            e.Property(x => x.CreatedAt).HasConversion(utc);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<ScreenshotTag>(e =>
        {
            e.HasKey(x => new { x.ScreenshotId, x.TagId });
            e.HasOne(x => x.Screenshot).WithMany(x => x.ScreenshotTags).HasForeignKey(x => x.ScreenshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tag).WithMany(x => x.ScreenshotTags).HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TagId);
        });

        b.Entity<ApplicationSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Value).IsRequired();
        });
    }
}

public sealed class SnaplyDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<SnaplyDbContext>
{
    public SnaplyDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SnaplyDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
