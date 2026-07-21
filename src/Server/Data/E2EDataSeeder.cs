using HouseConsensus.Shared;
using Microsoft.EntityFrameworkCore;

namespace HouseConsensus.Server.Data;

public static class E2EDataSeeder
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Listings.AnyAsync(x => x.ExternalId.StartsWith("e2e-"), ct)) return;

        var active = new Listing
        {
            ExternalId = "e2e-active",
            Address = "Testvej 10",
            City = "København",
            Price = 4_500_000m,
            FamilyFitScore = 91,
            AiAssessed = true,
            AiConfidence = 0.91,
            AiEvidence = "E2E active listing",
            ModelVersion = "e2e",
            RuleVersion = "e2e"
        };
        var rejected = new Listing
        {
            ExternalId = "e2e-rejected",
            Address = "Testvej 20",
            City = "Roskilde",
            Price = 6_500_000m,
            FamilyFitScore = 72,
            AiAssessed = true,
            AiConfidence = 0.95,
            AiEvidence = "E2E rejected listing",
            ModelVersion = "e2e",
            RuleVersion = "e2e"
        };
        rejected.ApplyImportDecision(true);
        db.Listings.AddRange(active, rejected);
        await db.SaveChangesAsync(ct);
    }
}
