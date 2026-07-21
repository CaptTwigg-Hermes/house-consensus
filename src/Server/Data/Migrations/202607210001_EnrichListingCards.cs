using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202607210001_EnrichListingCards")]
public sealed class EnrichListingCards : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings ADD COLUMN "PreviewImageUrl" text NULL;
ALTER TABLE listings ADD COLUMN "LivingArea" integer NULL;
ALTER TABLE listings ADD COLUMN "LotArea" integer NULL;
ALTER TABLE listings ADD COLUMN "Rooms" integer NULL;
ALTER TABLE listings ADD COLUMN "YearBuilt" integer NULL;
ALTER TABLE listings ADD COLUMN "Bathrooms" integer NULL;
ALTER TABLE listings ADD COLUMN "Bedrooms" integer NULL;
ALTER TABLE listings ADD COLUMN "Floors" integer NULL;
ALTER TABLE listings ADD COLUMN "EnergyLabel" text NULL;
ALTER TABLE listings ADD COLUMN "Quiet" boolean NULL;
ALTER TABLE listings ADD COLUMN "BuildableHeadroom" integer NULL;
ALTER TABLE listings ADD COLUMN "GroundFloorBedroom" boolean NULL;
ALTER TABLE listings ADD COLUMN "SeparateEntrance" boolean NULL;
ALTER TABLE listings ADD COLUMN "SecondKitchen" boolean NULL;
ALTER TABLE listings ADD COLUMN "PrivacyScore" integer NULL;
""");

    protected override void Down(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings DROP COLUMN "PrivacyScore", DROP COLUMN "SecondKitchen",
DROP COLUMN "SeparateEntrance", DROP COLUMN "GroundFloorBedroom",
DROP COLUMN "BuildableHeadroom", DROP COLUMN "Quiet", DROP COLUMN "EnergyLabel",
DROP COLUMN "Floors", DROP COLUMN "Bedrooms", DROP COLUMN "Bathrooms",
DROP COLUMN "YearBuilt", DROP COLUMN "Rooms", DROP COLUMN "LotArea",
DROP COLUMN "LivingArea", DROP COLUMN "PreviewImageUrl";
""");
}
