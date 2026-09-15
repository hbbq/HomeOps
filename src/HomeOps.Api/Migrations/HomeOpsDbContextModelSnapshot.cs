using HomeOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

#nullable disable

namespace HomeOps.Api.Migrations;

[DbContext(typeof(HomeOpsDbContext))]
partial class HomeOpsDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => ConfigureModel(modelBuilder);

    internal static void ConfigureModel(ModelBuilder modelBuilder)
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

        modelBuilder.Entity("HomeOps.Api.Data.Device", entity => entity.Navigation("MeasurementPoints"));
        modelBuilder.Entity("HomeOps.Api.Data.MeasurementPoint", entity => entity.Navigation("Measurements"));
    }
}
