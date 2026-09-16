using HomeOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeOps.Api.Migrations;

[DbContext(typeof(HomeOpsDbContext))]
[Migration("20260917020000_AddSmartThingsOAuth")]
partial class AddSmartThingsOAuth
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        HomeOpsDbContextModelSnapshot.ConfigureModel(modelBuilder);
}
