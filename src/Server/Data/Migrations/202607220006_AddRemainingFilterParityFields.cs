using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202607220006_AddRemainingFilterParityFields")]
public sealed class AddRemainingFilterParityFields : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings ADD COLUMN "PostalCode" varchar(16) NULL;
ALTER TABLE listings ADD COLUMN "Preferred" boolean NULL;
ALTER TABLE listings ADD COLUMN "IsNew" boolean NULL;
ALTER TABLE listings ADD COLUMN "FamilyUnits" varchar(64) NULL;
""");

    protected override void Down(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings DROP COLUMN "FamilyUnits";
ALTER TABLE listings DROP COLUMN "IsNew";
ALTER TABLE listings DROP COLUMN "Preferred";
ALTER TABLE listings DROP COLUMN "PostalCode";
""");
}
