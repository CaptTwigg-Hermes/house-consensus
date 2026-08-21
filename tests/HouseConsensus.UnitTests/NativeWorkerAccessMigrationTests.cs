using Xunit;

namespace HouseConsensus.UnitTests;

public sealed class NativeWorkerAccessMigrationTests
{
    private static readonly string Root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

    [Fact]
    public void Migration_grants_only_required_native_worker_tables_when_runtime_role_exists()
    {
        var path = Path.Combine(
            Root,
            "src/Server/Data/Migrations/202608210001_GrantNativeWorkerAccess.cs");

        Assert.True(File.Exists(path));
        var migration = File.ReadAllText(path);
        Assert.Contains("rolname = 'house_consensus'", migration, StringComparison.Ordinal);
        Assert.Contains(
            "GRANT SELECT, INSERT, UPDATE ON ingestion_runs, ingestion_source_snapshots, ingestion_stage_outcomes, listing_ingestion_projections, manual_scoring_jobs TO house_consensus",
            migration,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT ALL", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" TO PUBLIC", migration, StringComparison.OrdinalIgnoreCase);
    }
}
