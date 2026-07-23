using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;
[DbContext(typeof(AppDbContext)), Migration("202607230009_AddFullCommuteAndDelistedTombstones")]
public sealed class AddFullCommuteAndDelistedTombstones : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "CommuteJson" text;
CREATE TABLE IF NOT EXISTS delisted_listings (
    external_id text PRIMARY KEY,
    source_url text,
    verified_at timestamptz NOT NULL,
    verification_method text NOT NULL DEFAULT 'http_404'
);
""");

    protected override void Down(MigrationBuilder m) =>
        throw new NotSupportedException("Verified delisting tombstones cannot be rolled back safely.");
}
