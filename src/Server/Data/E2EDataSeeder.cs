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
            RuleVersion = "e2e",
            PreviewImageUrl = "https://images.example.test/house.webp",
            LivingArea = 186,
            LotArea = 920,
            Rooms = 7,
            YearBuilt = 1974,
            Bathrooms = 2,
            Bedrooms = 4,
            Floors = 1,
            EnergyLabel = "C",
            Quiet = true,
            BuildableHeadroom = 75,
            GroundFloorBedroom = true,
            SeparateEntrance = true,
            PrivacyScore = 4,
            FamilyPrivacyScore = 95,
            KidsSpaceScore = 95,
            GardenScore = 90,
            SharedLivingScore = 90,
            PracticalScore = 80,
            FamilyPrivacyWeight = 30,
            KidsSpaceWeight = 20,
            GardenWeight = 20,
            SharedLivingWeight = 15,
            PracticalWeight = 15,
            Latitude = 55.6761,
            Longitude = 12.5683,
            PostalCode = "2100",
            Preferred = true,
            IsNew = true,
            FamilyUnits = "two_family",
            MonthlyExpense = 4_500,
            DaysOnMarket = 12,
            CommuteMinutes = 20,
            BuildableStatus = "expand",
            Condition = "good",
            GardenOrientation = "southwest",
            MultigenFit = "strong"
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
            RuleVersion = "e2e",
            Latitude = 55.6419,
            Longitude = 12.0878,
            PostalCode = "4000",
            Preferred = false,
            IsNew = false,
            FamilyUnits = "two_family",
            Condition = "poor",
            MultigenFit = "unlikely"
        };
        rejected.ApplyImportDecision(true);
        db.Listings.AddRange(active, rejected);
        await db.SaveChangesAsync(ct);
    }
}
