using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202607220001_AddFamilyScoreBreakdown")]
public sealed class AddFamilyScoreBreakdown : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings ADD COLUMN "FamilyPrivacyScore" double precision NULL;
ALTER TABLE listings ADD COLUMN "KidsSpaceScore" double precision NULL;
ALTER TABLE listings ADD COLUMN "GardenScore" double precision NULL;
ALTER TABLE listings ADD COLUMN "SharedLivingScore" double precision NULL;
ALTER TABLE listings ADD COLUMN "PracticalScore" double precision NULL;
ALTER TABLE listings ADD COLUMN "FamilyPrivacyWeight" double precision NULL;
ALTER TABLE listings ADD COLUMN "KidsSpaceWeight" double precision NULL;
ALTER TABLE listings ADD COLUMN "GardenWeight" double precision NULL;
ALTER TABLE listings ADD COLUMN "SharedLivingWeight" double precision NULL;
ALTER TABLE listings ADD COLUMN "PracticalWeight" double precision NULL;
""");

    protected override void Down(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings DROP COLUMN "PracticalWeight", DROP COLUMN "SharedLivingWeight",
DROP COLUMN "GardenWeight", DROP COLUMN "KidsSpaceWeight", DROP COLUMN "FamilyPrivacyWeight",
DROP COLUMN "PracticalScore", DROP COLUMN "SharedLivingScore", DROP COLUMN "GardenScore",
DROP COLUMN "KidsSpaceScore", DROP COLUMN "FamilyPrivacyScore";
""");
}
