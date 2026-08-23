using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608230002_GrantNativeNoiseWorkerAccess")]
public sealed class GrantNativeNoiseWorkerAccess : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'house_consensus')
       AND to_regnamespace('noise') IS NOT NULL
       AND to_regclass('noise.noise_source') IS NOT NULL
       AND to_regclass('noise.noise_areas') IS NOT NULL
       AND to_regclass('public.property_noise_samples') IS NOT NULL THEN
        GRANT USAGE ON SCHEMA noise TO house_consensus;
        GRANT SELECT ON noise.noise_source, noise.noise_areas TO house_consensus;
        GRANT SELECT, INSERT, UPDATE ON public.property_noise_samples TO house_consensus;
    END IF;
END
$$;
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Native worker noise access is an operational security boundary and cannot be rolled back implicitly.");
}
