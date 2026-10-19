using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ACopy.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<BackupTarget> Targets => Set<BackupTarget>();
    public DbSet<BackupRun> Runs => Set<BackupRun>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<EncryptionKey> Keys => Set<EncryptionKey>();
    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<TargetDestination> TargetDestinations => Set<TargetDestination>();
    public DbSet<RunUpload> Uploads => Set<RunUpload>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BackupTarget>(e =>
        {
            e.Property(x => x.Engine).HasConversion<string>();
            e.Property(x => x.Mode).HasConversion<string>();
            e.Property(x => x.Naming).HasConversion<string>();
            e.Property(x => x.Compression).HasConversion<string>();
            e.Property(x => x.Source).HasConversion<string>();
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.LabelKey).IsUnique();
            // SQLite cannot ORDER BY / compare DateTimeOffset; store UTC ticks instead
            e.Property(x => x.CreatedAt).HasConversion(DateTimeOffsetToUtcTicks);
            e.Property(x => x.UpdatedAt).HasConversion(DateTimeOffsetToUtcTicks);
        });

        b.Entity<BackupRun>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Trigger).HasConversion<string>();
            e.Property(x => x.StartedAt).HasConversion(DateTimeOffsetToUtcTicks);
            e.Property(x => x.FinishedAt).HasConversion(NullableDateTimeOffsetToUtcTicks);
            e.HasOne(x => x.Target).WithMany(t => t.Runs).HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TargetId, x.StartedAt });
            e.HasIndex(x => x.StartedAt);
        });

        b.Entity<BackupTarget>()
            .HasOne(t => t.EncryptionKey).WithMany().HasForeignKey(t => t.EncryptionKeyId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<BackupRun>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasMany(x => x.Uploads).WithOne(u => u.Run).HasForeignKey(u => u.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EncryptionKey>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.CreatedAt).HasConversion(DateTimeOffsetToUtcTicks);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Destination>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.CreatedAt).HasConversion(DateTimeOffsetToUtcTicks);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<TargetDestination>(e =>
        {
            e.HasKey(x => new { x.TargetId, x.DestinationId });
            e.HasOne(x => x.Target).WithMany(t => t.Destinations).HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Destination).WithMany().HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RunUpload>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.FinishedAt).HasConversion(NullableDateTimeOffsetToUtcTicks);
            e.HasOne(x => x.Destination).WithMany().HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.DestinationId, x.Status });
        });

        b.Entity<AppSetting>().HasKey(x => x.Key);
    }

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long> DateTimeOffsetToUtcTicks =
        new(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?> NullableDateTimeOffsetToUtcTicks =
        new(v => v.HasValue ? v.Value.UtcTicks : null, v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without starting the app.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=design.db").Options);
}
