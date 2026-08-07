using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608070003_AddNativeListingProjection")]
public sealed class AddNativeListingProjection : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
CREATE TABLE IF NOT EXISTS listing_ingestion_projections (
    listing_id uuid PRIMARY KEY,
    source_system text NOT NULL CHECK (length(btrim(source_system)) > 0),
    source_scope text NOT NULL CHECK (length(btrim(source_scope)) > 0),
    source_record_id text NOT NULL CHECK (length(btrim(source_record_id)) > 0),
    source_snapshot_id uuid NOT NULL,
    projected_at timestamptz NOT NULL,
    FOREIGN KEY (listing_id) REFERENCES listings("Id") ON DELETE RESTRICT,
    FOREIGN KEY (source_snapshot_id) REFERENCES ingestion_source_snapshots(snapshot_id) ON DELETE RESTRICT,
    UNIQUE (source_system, source_scope, source_record_id)
);
""");

    protected override void Down(MigrationBuilder m) =>
        throw new NotSupportedException("Native source identities cannot be removed safely from projected listings.");
}
