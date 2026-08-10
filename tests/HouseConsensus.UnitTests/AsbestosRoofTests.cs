using HouseConsensus.Shared;
using Xunit;

namespace HouseConsensus.UnitTests;

public sealed class AsbestosRoofTests
{
    [Fact]
    public void Member_correction_replaces_effective_state_without_creating_history()
    {
        var listing = new Listing { ExternalId = "one", Address = "One" };

        listing.SetAsbestosRoofCorrection(AsbestosRoofStatus.Possible);
        listing.SetAsbestosRoofCorrection(AsbestosRoofStatus.NoIndication);

        Assert.Equal(AsbestosRoofStatus.NoIndication, listing.AsbestosRoofCorrection);
        Assert.DoesNotContain(typeof(Listing).GetProperties(), property => property.Name.Contains("CorrectionHistory", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_member_correction_restores_automated_assessment()
    {
        var dto = Listing(AsbestosRoofStatus.Likely, AsbestosRoofStatus.Possible);

        Assert.Equal(AsbestosRoofStatus.Possible, dto.EffectiveAsbestosRoofStatus);
        Assert.True(dto.AsbestosRoofHumanCorrected);
        Assert.Equal(AsbestosRoofStatus.Likely, dto.AutomatedAsbestosRoofStatus);

        Assert.Equal(AsbestosRoofStatus.Likely, Listing(AsbestosRoofStatus.Likely, null).EffectiveAsbestosRoofStatus);
    }

    [Fact]
    public void Migration_and_endpoint_store_only_the_current_confirmed_correction()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var migrationPath = Path.Combine(root, "src/Server/Data/Migrations/202608100002_AddAsbestosRoofAssessments.cs");

        Assert.True(File.Exists(migrationPath));
        var migration = File.ReadAllText(migrationPath);
        var program = File.ReadAllText(Path.Combine(root, "src/Server/Program.cs"));
        Assert.Contains("asbestos_roof_assessments", migration, StringComparison.Ordinal);
        Assert.Contains("AsbestosRoofCorrection", migration, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO asbestos_roof_assessments", migration, StringComparison.Ordinal);
        Assert.Contains("'unknown'", migration, StringComparison.Ordinal);
        Assert.Contains("'epoch'::timestamptz", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("now()", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE IF NOT EXISTS asbestos_roof_assessments", migration, StringComparison.Ordinal);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM pg_constraint", migration, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_asbestos_roof_assessments_latest", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("CorrectedBy", migration, StringComparison.Ordinal);
        Assert.Contains("if (!request.Confirmed)", program, StringComparison.Ordinal);
    }

    private static ListingDto Listing(AsbestosRoofStatus automated, AsbestosRoofStatus? correction) => new(
        Guid.NewGuid(), "case", "Address", "City", 1, null,
        ListingState.Active, false, null, null, null, null, null, false, [],
        AutomatedAsbestosRoofStatus: automated,
        AsbestosRoofCorrection: correction,
        AsbestosRoofConfidence: .9,
        AsbestosRoofPrimarySource: "structured",
        AsbestosRoofEvidence: "[]",
        AsbestosRoofRuleVersion: "asbestos-roof-v1",
        AsbestosRoofAssessedAt: DateTimeOffset.UtcNow);
}
