using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;
[DbContext(typeof(AppDbContext)), Migration("202607220008_PreserveAiApplicationIdentity")]
public sealed class PreserveAiApplicationIdentity : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE ai_rule_applications ADD COLUMN "ListingExternalId" text;
UPDATE ai_rule_applications a SET "ListingExternalId" = l."ExternalId" FROM listings l WHERE l."Id" = a."ListingId";
ALTER TABLE ai_rule_applications ALTER COLUMN "ListingExternalId" SET NOT NULL;
DO $$ DECLARE fk_name text; BEGIN
    SELECT conname INTO fk_name FROM pg_constraint
    WHERE conrelid = 'ai_rule_applications'::regclass AND confrelid = 'listings'::regclass AND contype = 'f';
    IF fk_name IS NOT NULL THEN EXECUTE format('ALTER TABLE ai_rule_applications DROP CONSTRAINT %I', fk_name); END IF;
END $$;
""");
    protected override void Down(MigrationBuilder m) =>
        throw new NotSupportedException("Immutable AI application provenance cannot be rolled back safely.");
}
