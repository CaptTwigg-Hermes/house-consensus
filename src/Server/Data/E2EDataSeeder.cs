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
            AiEvidence = "{\"decision\":\"pass\",\"confidence\":\"high\",\"model_version\":\"e2e\",\"evidence\":{\"vision_summary\":\"Two private household zones with shared living space.\",\"vision_separate_entrance\":true,\"vision_ground_floor_bedroom\":true}}",
            ModelVersion = "e2e",
            RuleVersion = "e2e",
            SourceUrl = "https://listing.example.test/e2e-active",
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
            CommuteJson = "{\"status\":\"ok\",\"destinations\":{\"hoje_taastrup_st\":{\"label\":\"Høje Taastrup St.\",\"car\":{\"min\":20,\"km\":18.2},\"public\":{\"min\":31,\"transfers\":1},\"bike\":{\"min\":52,\"km\":16.8}},\"stamholmen\":{\"label\":\"Stamholmen\",\"car\":{\"min\":24,\"km\":21.4},\"public\":{\"min\":46,\"transfers\":2},\"bike\":{\"min\":61,\"km\":20.1}}}}",
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
            AiEvidence = "{\"vision_summary\":\"Single-family layout with no private second household zone.\",\"vision_multigen_layout\":\"unlikely\",\"two_family_reasons\":[\"No separate entrance\",\"No second kitchen\"]}",
            ModelVersion = "e2e",
            RuleVersion = "e2e",
            SourceUrl = "https://listing.example.test/e2e-rejected",
            PreviewImageUrl = "https://images.example.test/rejected.webp",
            LivingArea = 164,
            LotArea = 740,
            Rooms = 6,
            YearBuilt = 1968,
            Bathrooms = 1,
            Bedrooms = 4,
            Floors = 2,
            EnergyLabel = "D",
            MonthlyExpense = 5_200,
            DaysOnMarket = 31,
            CommuteMinutes = 28,
            CommuteJson = "{\"status\":\"ok\",\"destinations\":{\"hoje_taastrup_st\":{\"label\":\"Høje Taastrup St.\",\"car\":{\"min\":28,\"km\":24.2},\"public\":{\"min\":39,\"transfers\":1},\"bike\":{\"min\":72,\"km\":22.5}},\"stamholmen\":{\"label\":\"Stamholmen\",\"car\":{\"min\":34,\"km\":29.1},\"public\":{\"min\":55,\"transfers\":2},\"bike\":{\"min\":84,\"km\":27.7}}}}",
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
