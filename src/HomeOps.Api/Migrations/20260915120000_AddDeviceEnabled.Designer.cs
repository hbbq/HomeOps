using HomeOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeOps.Api.Migrations;

[DbContext(typeof(HomeOpsDbContext))]
[Migration("20260915120000_AddDeviceEnabled")]
partial class AddDeviceEnabled
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        HomeOpsDbContextModelSnapshot.ConfigureModel(modelBuilder);
}
