using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608030001_AddVoteOverallScore")]
public sealed class AddVoteOverallScore : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE votes ADD COLUMN IF NOT EXISTS "OverallScore" integer NULL;
ALTER TABLE votes DROP CONSTRAINT IF EXISTS "CK_votes_OverallScore";
ALTER TABLE votes ADD CONSTRAINT "CK_votes_OverallScore" CHECK ("OverallScore" IS NULL OR ("OverallScore" >= 1 AND "OverallScore" <= 5));
""");

    protected override void Down(MigrationBuilder m) => m.Sql("""
ALTER TABLE votes DROP CONSTRAINT IF EXISTS "CK_votes_OverallScore";
ALTER TABLE votes DROP COLUMN IF EXISTS "OverallScore";
""");
}
