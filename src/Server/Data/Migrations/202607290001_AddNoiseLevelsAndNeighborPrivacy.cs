using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202607290001_AddNoiseLevelsAndNeighborPrivacy")]
public sealed class AddNoiseLevelsAndNeighborPrivacy : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RoadNoiseDb" double precision NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RailNoiseDb" double precision NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "AirNoiseDb" double precision NULL;
ALTER TYPE reason_tag ADD VALUE IF NOT EXISTS 'privacy_from_neighbors';
""");

    protected override void Down(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings DROP COLUMN IF EXISTS "AirNoiseDb";
ALTER TABLE listings DROP COLUMN IF EXISTS "RailNoiseDb";
ALTER TABLE listings DROP COLUMN IF EXISTS "RoadNoiseDb";
""");
}
