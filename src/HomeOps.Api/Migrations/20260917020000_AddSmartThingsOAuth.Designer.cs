using HomeOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeOps.Api.Migrations;

[DbContext(typeof(HomeOpsDbContext))]
[Migration("20260917020000_AddSmartThingsOAuth")]
partial class AddSmartThingsOAuth
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "8.0.1").HasAnnotation("Relational:MaxIdentifierLength", 128);
        SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

        modelBuilder.Entity("HomeOps.Api.Data.Device", entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("int");
            SqlServerPropertyBuilderExtensions.UseIdentityColumn(entity.Property<int>("Id"));
            entity.Property<bool>("IsEnabled").ValueGeneratedOnAdd().HasColumnType("bit").HasDefaultValue(true);
            entity.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("nvarchar(200)");
            entity.Property<string>("Source").IsRequired().HasMaxLength(100).HasColumnType("nvarchar(100)");
            entity.Property<string>("SourceDeviceId").IsRequired().HasMaxLength(200).HasColumnType("nvarchar(200)");
            entity.HasKey("Id");
            entity.HasIndex("Source", "SourceDeviceId").IsUnique();
            entity.ToTable("Devices");
        });

        modelBuilder.Entity("HomeOps.Api.Data.MeasurementPoint", entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("int");
            SqlServerPropertyBuilderExtensions.UseIdentityColumn(entity.Property<int>("Id"));
            entity.Property<int>("DeviceId").HasColumnType("int");
            entity.Property<string>("Key").IsRequired().HasMaxLength(100).HasColumnType("nvarchar(100)");
            entity.Property<string>("Kind").IsRequired().HasMaxLength(50).HasColumnType("nvarchar(50)");
            entity.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("nvarchar(200)");
            entity.Property<string>("Unit").HasMaxLength(50).HasColumnType("nvarchar(50)");
            entity.HasKey("Id");
            entity.HasIndex("DeviceId", "Key").IsUnique();
            entity.ToTable("MeasurementPoints");
        });

        modelBuilder.Entity("HomeOps.Api.Data.ExposedMeasurement", entity =>
        {
            entity.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint");
            SqlServerPropertyBuilderExtensions.UseIdentityColumn(entity.Property<long>("Id"));
            entity.Property<int>("MeasurementPointId").HasColumnType("int");
            entity.Property<DateTimeOffset>("Timestamp").HasColumnType("datetimeoffset");
            entity.Property<decimal>("Value").HasPrecision(18, 4).HasColumnType("decimal(18,4)");
            entity.HasKey("Id");
            entity.HasIndex("MeasurementPointId", "Id");
            entity.ToTable("ExposedMeasurements");
        });

        modelBuilder.Entity("HomeOps.Api.Data.Measurement", entity =>
        {
            entity.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint");
            SqlServerPropertyBuilderExtensions.UseIdentityColumn(entity.Property<long>("Id"));
            entity.Property<int>("MeasurementPointId").HasColumnType("int");
            entity.Property<DateTimeOffset>("Timestamp").HasColumnType("datetimeoffset");
            entity.Property<decimal>("Value").HasPrecision(18, 4).HasColumnType("decimal(18,4)");
            entity.HasKey("Id");
            entity.HasIndex("MeasurementPointId", "Timestamp");
            entity.ToTable("Measurements");
        });

        modelBuilder.Entity("HomeOps.Api.Data.MotionHold", entity =>
        {
            entity.Property<int>("MeasurementPointId").HasColumnType("int");
            entity.Property<DateTimeOffset>("DueAt").HasColumnType("datetimeoffset");
            entity.HasKey("MeasurementPointId");
            entity.HasIndex("DueAt");
            entity.ToTable("MotionHolds");
        });

        modelBuilder.Entity("HomeOps.Api.Data.SmartThingsAuthorization", entity =>
        {
            entity.Property<int>("Id").ValueGeneratedNever().HasColumnType("int");
            entity.Property<DateTimeOffset>("AccessTokenExpiresAt").HasColumnType("datetimeoffset");
            entity.Property<string>("InstalledAppId").HasMaxLength(200).HasColumnType("nvarchar(200)");
            entity.Property<string>("ProtectedAccessToken").IsRequired().HasColumnType("nvarchar(max)");
            entity.Property<string>("ProtectedRefreshToken").IsRequired().HasColumnType("nvarchar(max)");
            entity.Property<bool>("RequiresReauthorization").HasColumnType("bit");
            entity.Property<string>("Scope").HasMaxLength(500).HasColumnType("nvarchar(500)");
            entity.Property<DateTimeOffset>("UpdatedAt").HasColumnType("datetimeoffset");
            entity.HasKey("Id");
            entity.ToTable("SmartThingsAuthorizations");
        });

        modelBuilder.Entity("HomeOps.Api.Data.SmartThingsAuthorizationState", entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("int");
            SqlServerPropertyBuilderExtensions.UseIdentityColumn(entity.Property<int>("Id"));
            entity.Property<DateTimeOffset>("ExpiresAt").HasColumnType("datetimeoffset");
            entity.Property<string>("StateHash").IsRequired().HasMaxLength(64).HasColumnType("nvarchar(64)");
            entity.HasKey("Id");
            entity.HasIndex("ExpiresAt");
            entity.HasIndex("StateHash").IsUnique();
            entity.ToTable("SmartThingsAuthorizationStates");
        });

        modelBuilder.Entity("HomeOps.Api.Data.MeasurementPoint", entity =>
        {
            entity.HasOne("HomeOps.Api.Data.Device", "Device").WithMany("MeasurementPoints").HasForeignKey("DeviceId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            entity.Navigation("Device");
        });

        modelBuilder.Entity("HomeOps.Api.Data.Measurement", entity =>
        {
            entity.HasOne("HomeOps.Api.Data.MeasurementPoint", "MeasurementPoint").WithMany("Measurements").HasForeignKey("MeasurementPointId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            entity.Navigation("MeasurementPoint");
        });

        modelBuilder.Entity("HomeOps.Api.Data.ExposedMeasurement", entity =>
        {
            entity.HasOne("HomeOps.Api.Data.MeasurementPoint", "MeasurementPoint").WithMany("ExposedMeasurements").HasForeignKey("MeasurementPointId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            entity.Navigation("MeasurementPoint");
        });

        modelBuilder.Entity("HomeOps.Api.Data.MotionHold", entity =>
        {
            entity.HasOne("HomeOps.Api.Data.MeasurementPoint", "MeasurementPoint").WithOne("MotionHold").HasForeignKey("HomeOps.Api.Data.MotionHold", "MeasurementPointId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            entity.Navigation("MeasurementPoint");
        });

        modelBuilder.Entity("HomeOps.Api.Data.Device", entity => entity.Navigation("MeasurementPoints"));
        modelBuilder.Entity("HomeOps.Api.Data.MeasurementPoint", entity =>
        {
            entity.Navigation("ExposedMeasurements");
            entity.Navigation("Measurements");
            entity.Navigation("MotionHold");
        });
    }
}
