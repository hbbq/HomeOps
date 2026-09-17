using Microsoft.EntityFrameworkCore;

namespace HomeOps.Api.Data;

public sealed class HomeOpsDbContext(DbContextOptions<HomeOpsDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<MeasurementPoint> MeasurementPoints => Set<MeasurementPoint>();
    public DbSet<Measurement> Measurements => Set<Measurement>();
    public DbSet<ExposedMeasurement> ExposedMeasurements => Set<ExposedMeasurement>();
    public DbSet<MotionHold> MotionHolds => Set<MotionHold>();
    public DbSet<SmartThingsAuthorization> SmartThingsAuthorizations => Set<SmartThingsAuthorization>();
    public DbSet<SmartThingsAuthorizationState> SmartThingsAuthorizationStates => Set<SmartThingsAuthorizationState>();

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

        modelBuilder.Entity<ExposedMeasurement>(entity =>
        {
            entity.Property(x => x.Value).HasPrecision(18, 4);
            entity.HasIndex(x => new { x.MeasurementPointId, x.Id });
            entity.HasOne(x => x.MeasurementPoint)
                .WithMany(x => x.ExposedMeasurements)
                .HasForeignKey(x => x.MeasurementPointId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MotionHold>(entity =>
        {
            entity.HasKey(x => x.MeasurementPointId);
            entity.HasIndex(x => x.DueAt);
            entity.HasOne(x => x.MeasurementPoint)
                .WithOne(x => x.MotionHold)
                .HasForeignKey<MotionHold>(x => x.MeasurementPointId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SmartThingsAuthorization>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.InstalledAppId).HasMaxLength(200);
            entity.Property(x => x.Scope).HasMaxLength(500);
        });

        modelBuilder.Entity<SmartThingsAuthorizationState>(entity =>
        {
            entity.Property(x => x.StateHash).HasMaxLength(64);
            entity.HasIndex(x => x.StateHash).IsUnique();
            entity.HasIndex(x => x.ExpiresAt);
        });
    }
}
