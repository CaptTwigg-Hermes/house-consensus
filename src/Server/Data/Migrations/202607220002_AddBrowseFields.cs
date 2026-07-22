using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;
[DbContext(typeof(AppDbContext)), Migration("202607220002_AddBrowseFields")]
public sealed class AddBrowseFields : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings ADD COLUMN "Latitude" double precision NULL;
ALTER TABLE listings ADD COLUMN "Longitude" double precision NULL;
ALTER TABLE listings ADD COLUMN "MonthlyExpense" integer NULL;
ALTER TABLE listings ADD COLUMN "DaysOnMarket" integer NULL;
ALTER TABLE listings ADD COLUMN "CommuteMinutes" integer NULL;
ALTER TABLE listings ADD COLUMN "BuildableStatus" varchar(64) NULL;
ALTER TABLE listings ADD COLUMN "Condition" varchar(64) NULL;
ALTER TABLE listings ADD COLUMN "GardenOrientation" varchar(64) NULL;
ALTER TABLE listings ADD COLUMN "MultigenFit" varchar(64) NULL;
""");
    protected override void Down(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings DROP COLUMN "MultigenFit", DROP COLUMN "GardenOrientation",
DROP COLUMN "Condition", DROP COLUMN "BuildableStatus", DROP COLUMN "CommuteMinutes",
DROP COLUMN "DaysOnMarket", DROP COLUMN "MonthlyExpense", DROP COLUMN "Longitude",
DROP COLUMN "Latitude";
""");
}
