using Microsoft.EntityFrameworkCore;

namespace HomeOps.Api.Data;

public sealed class HomeOpsDbContext(DbContextOptions<HomeOpsDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<MeasurementPoint> MeasurementPoints => Set<MeasurementPoint>();
    public DbSet<Measurement> Measurements => Set<Measurement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>(entity =>
        {
            entity.Property(x => x.IsEnabled).HasDefaultValue(true);
            entity.Property(x => x.Source).HasMaxLength(100);
            entity.Property(x => x.SourceDeviceId).HasMaxLength(200);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.HasIndex(x => new { x.Source, x.SourceDeviceId }).IsUnique();
        });

        modelBuilder.Entity<MeasurementPoint>(entity =>
        {
            entity.Property(x => x.Key).HasMaxLength(100);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Kind).HasMaxLength(50);
            entity.Property(x => x.Unit).HasMaxLength(50);
            entity.HasIndex(x => new { x.DeviceId, x.Key }).IsUnique();
            entity.HasOne(x => x.Device)
                .WithMany(x => x.MeasurementPoints)
                .HasForeignKey(x => x.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Measurement>(entity =>
        {
            entity.Property(x => x.Value).HasPrecision(18, 4);
            entity.HasIndex(x => new { x.MeasurementPointId, x.Timestamp });
            entity.HasOne(x => x.MeasurementPoint)
                .WithMany(x => x.Measurements)
                .HasForeignKey(x => x.MeasurementPointId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
