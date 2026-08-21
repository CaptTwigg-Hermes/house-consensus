using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608210001_GrantNativeWorkerAccess")]
public sealed class GrantNativeWorkerAccess : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'house_consensus') THEN
        GRANT SELECT, INSERT, UPDATE ON ingestion_runs, ingestion_source_snapshots, ingestion_stage_outcomes, listing_ingestion_projections, manual_scoring_jobs TO house_consensus;
    END IF;
END
$$;
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Native worker access is an operational security boundary and cannot be rolled back implicitly.");
}
