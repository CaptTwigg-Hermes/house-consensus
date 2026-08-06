using HouseConsensus.Server.Scoring;
using HouseConsensus.Shared;
using Xunit;

namespace HouseConsensus.UnitTests;

public sealed class FamilyScorerTests
{
    [Fact]
    public void Complete_evidence_scores_each_fact_once_and_uses_loudest_noise_source()
    {
        var result = FamilyScorer.Score(CompleteInput());

        Assert.Equal(ScoreStatus.Complete, result.Status);
        Assert.Equal(100, result.Privacy.Score);
        Assert.Equal(100, result.KidsSpace.Score);
        Assert.Equal(100, result.Garden.Score);
        Assert.Equal(80, result.SharedLiving.Score);
        Assert.Equal(65, result.Practical.Score);
        Assert.Equal(91.8, result.Total);
        Assert.Equal(100, result.CoveragePct);
        Assert.Contains(result.Practical.Notes, note => note.Contains("rail=67", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Practical.Notes, note => note.Contains("quiet", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(EvidenceAssessmentStatus.Unavailable)]
    [InlineData(EvidenceAssessmentStatus.Failed)]
    [InlineData(EvidenceAssessmentStatus.Unreadable)]
    public void Missing_failed_or_unreadable_privacy_evidence_never_becomes_zero(
        EvidenceAssessmentStatus status)
    {
        var input = CompleteInput() with
        {
            Privacy = CompleteInput().Privacy with { Status = status }
        };

        var result = FamilyScorer.Score(input);

        Assert.Equal(ScoreStatus.Incomplete, result.Status);
        Assert.Null(result.Total);
        Assert.Null(result.Privacy.Score);
        Assert.False(result.Privacy.Available);
        Assert.Equal(70, result.CoveragePct);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void Bathroom_fallback_scores_explicit_count_once_when_ensuite_count_is_zero(
        int bathroomCount,
        double expectedPrivacy)
    {
        var input = CompleteInput() with
        {
            Privacy = new PrivacyScoreInput(
                EvidenceAssessmentStatus.Complete,
                SeparateEntrance: false,
                SecondKitchen: false,
                InternalConnection: false,
                SplitType: DwellingSplitType.None,
                TwoDwellings: false,
                EnsuiteCount: 0,
                BathroomCount: bathroomCount,
                StaircaseCount: 0)
        };

        var result = FamilyScorer.Score(input);

        Assert.Equal(ScoreStatus.Complete, result.Status);
        Assert.Equal(expectedPrivacy, result.Privacy.Score);
    }

    [Fact]
    public void Explicit_negative_privacy_facts_are_a_real_zero()
    {
        var input = CompleteInput() with
        {
            Privacy = new PrivacyScoreInput(
                EvidenceAssessmentStatus.Complete,
                SeparateEntrance: false,
                SecondKitchen: false,
                InternalConnection: false,
                SplitType: DwellingSplitType.None,
                TwoDwellings: false,
                EnsuiteCount: 0,
                BathroomCount: 1,
                StaircaseCount: 0)
        };

        var result = FamilyScorer.Score(input);

        Assert.Equal(ScoreStatus.Complete, result.Status);
        Assert.Equal(0, result.Privacy.Score);
        Assert.NotNull(result.Total);
    }

    [Fact]
    public void Complete_explicit_zero_facts_publish_a_valid_numeric_zero()
    {
        var input = new FamilyScoreInput(
            new PrivacyScoreInput(EvidenceAssessmentStatus.Complete, false, false, false,
                DwellingSplitType.None, false, 0, 1, 0),
            new KidsSpaceScoreInput(EvidenceAssessmentStatus.Complete, 0, 0, false, 1, false, false),
            new GardenScoreInput(EvidenceAssessmentStatus.Complete, 0, false, false, false, false),
            new SharedLivingScoreInput(EvidenceAssessmentStatus.Complete, false, 0, KitchenSize.Small,
                PropertyCondition.Poor),
            new PracticalScoreInput(EvidenceAssessmentStatus.Complete, 0, false, false, false, 0,
                "G", CoveredNoise(70), CoveredNoise(71), CoveredNoise(72)),
            BrokerContradiction: false);

        var result = FamilyScorer.Score(input);

        Assert.Equal(ScoreStatus.Complete, result.Status);
        Assert.Equal(0, result.Total);
        Assert.All(result.Dimensions, dimension => Assert.Equal(0, dimension.Score));
    }

    [Fact]
    public void Terrace_fact_is_scored_once_in_the_garden_dimension()
    {
        var baseline = CompleteInput() with
        {
            KidsSpace = CompleteInput().KidsSpace with { Rooms = 6, HousingAreaM2 = 150, Basement = false, Floors = 1, Storage = false, UtilityRoom = false },
            Garden = CompleteInput().Garden with { LotAreaM2 = 400, HasStructure = false, PrivateZones = false }
        };
        var withoutTerrace = FamilyScorer.Score(baseline with
        {
            Garden = baseline.Garden with { Terrace = false }
        });
        var withTerrace = FamilyScorer.Score(baseline with
        {
            Garden = baseline.Garden with { Terrace = true }
        });

        Assert.Equal(withoutTerrace.KidsSpace.Score, withTerrace.KidsSpace.Score);
        Assert.Equal(withoutTerrace.Garden.Score + 8, withTerrace.Garden.Score);
    }

    [Fact]
    public void Broker_contradiction_requires_review_and_suppresses_total()
    {
        var result = FamilyScorer.Score(CompleteInput() with { BrokerContradiction = true });

        Assert.Equal(ScoreStatus.NeedsReview, result.Status);
        Assert.Null(result.Total);
        Assert.Contains("broker_contradiction", result.BlockingReasons);
    }

    [Fact]
    public void Null_dimension_payload_fails_closed()
    {
        var input = CompleteInput() with { Privacy = null! };

        var result = FamilyScorer.Score(input);

        Assert.Equal(ScoreStatus.NeedsReview, result.Status);
        Assert.Null(result.Total);
        Assert.Contains("invalid_privacy_evidence", result.BlockingReasons);
    }

    [Fact]
    public void Null_noise_payload_fails_closed()
    {
        var input = CompleteInput() with
        {
            Practical = CompleteInput().Practical with { RailNoise = null! }
        };

        var result = FamilyScorer.Score(input);

        Assert.Equal(ScoreStatus.NeedsReview, result.Status);
        Assert.Null(result.Total);
        Assert.Contains("invalid_practical_evidence", result.BlockingReasons);
    }

    [Fact]
    public void Unknown_energy_label_fails_closed()
    {
        var input = CompleteInput() with
        {
            Practical = CompleteInput().Practical with { EnergyLabel = "not-a-label" }
        };

        var result = FamilyScorer.Score(input);

        Assert.Equal(ScoreStatus.NeedsReview, result.Status);
        Assert.Null(result.Total);
        Assert.Contains("invalid_practical_evidence", result.BlockingReasons);
    }

    [Fact]
    public void Malformed_evidence_fails_closed()
    {
        var input = CompleteInput() with
        {
            KidsSpace = CompleteInput().KidsSpace with { Rooms = -1 }
        };

        var result = FamilyScorer.Score(input);

        Assert.Equal(ScoreStatus.NeedsReview, result.Status);
        Assert.Null(result.Total);
        Assert.Contains("invalid_kids_space_evidence", result.BlockingReasons);
    }

    [Theory]
    [MemberData(nameof(MissingScoreBearingFacts))]
    public void Complete_dimension_rejects_each_unknown_score_bearing_fact(MissingFact fact)
    {
        var original = CompleteInput();
        var input = fact switch
        {
            MissingFact.PrivacySeparateEntrance => original with { Privacy = original.Privacy with { SeparateEntrance = null } },
            MissingFact.PrivacySecondKitchen => original with { Privacy = original.Privacy with { SecondKitchen = null } },
            MissingFact.PrivacyInternalConnection => original with { Privacy = original.Privacy with { InternalConnection = null } },
            MissingFact.PrivacySplitType => original with { Privacy = original.Privacy with { SplitType = DwellingSplitType.Unknown } },
            MissingFact.PrivacyTwoDwellings => original with { Privacy = original.Privacy with { TwoDwellings = null } },
            MissingFact.PrivacyEnsuiteCount => original with { Privacy = original.Privacy with { EnsuiteCount = null } },
            MissingFact.PrivacyBathroomCount => original with { Privacy = original.Privacy with { BathroomCount = null } },
            MissingFact.PrivacyStaircaseCount => original with { Privacy = original.Privacy with { StaircaseCount = null } },
            MissingFact.KidsBasement => original with { KidsSpace = original.KidsSpace with { Basement = null } },
            MissingFact.KidsFloors => original with { KidsSpace = original.KidsSpace with { Floors = null } },
            MissingFact.KidsStorage => original with { KidsSpace = original.KidsSpace with { Storage = null } },
            MissingFact.KidsUtilityRoom => original with { KidsSpace = original.KidsSpace with { UtilityRoom = null } },
            MissingFact.GardenStructure => original with { Garden = original.Garden with { HasStructure = null } },
            MissingFact.GardenPrivateZones => original with { Garden = original.Garden with { PrivateZones = null } },
            MissingFact.GardenTerrace => original with { Garden = original.Garden with { Terrace = null } },
            MissingFact.GardenHasGarden => original with { Garden = original.Garden with { HasGarden = null } },
            MissingFact.SharedOpenPlan => original with { SharedLiving = original.SharedLiving with { OpenPlan = null } },
            MissingFact.SharedDiningCapacity => original with { SharedLiving = original.SharedLiving with { DiningCapacity = null } },
            MissingFact.SharedKitchenSize => original with { SharedLiving = original.SharedLiving with { KitchenSize = KitchenSize.Unknown } },
            MissingFact.SharedCondition => original with { SharedLiving = original.SharedLiving with { Condition = PropertyCondition.Unknown } },
            MissingFact.PracticalGarageOnPlan => original with { Practical = original.Practical with { GarageOnPlan = null } },
            MissingFact.PracticalGarageVisible => original with { Practical = original.Practical with { GarageVisible = null } },
            _ => throw new ArgumentOutOfRangeException(nameof(fact))
        };

        var result = FamilyScorer.Score(input);

        Assert.Equal(ScoreStatus.NeedsReview, result.Status);
        Assert.Null(result.Total);
    }

    public static TheoryData<MissingFact> MissingScoreBearingFacts => new()
    {
        MissingFact.PrivacySeparateEntrance,
        MissingFact.PrivacySecondKitchen,
        MissingFact.PrivacyInternalConnection,
        MissingFact.PrivacySplitType,
        MissingFact.PrivacyTwoDwellings,
        MissingFact.PrivacyEnsuiteCount,
        MissingFact.PrivacyBathroomCount,
        MissingFact.PrivacyStaircaseCount,
        MissingFact.KidsBasement,
        MissingFact.KidsFloors,
        MissingFact.KidsStorage,
        MissingFact.KidsUtilityRoom,
        MissingFact.GardenStructure,
        MissingFact.GardenPrivateZones,
        MissingFact.GardenTerrace,
        MissingFact.GardenHasGarden,
        MissingFact.SharedOpenPlan,
        MissingFact.SharedDiningCapacity,
        MissingFact.SharedKitchenSize,
        MissingFact.SharedCondition,
        MissingFact.PracticalGarageOnPlan,
        MissingFact.PracticalGarageVisible
    };

    public enum MissingFact
    {
        PrivacySeparateEntrance, PrivacySecondKitchen, PrivacyInternalConnection, PrivacySplitType,
        PrivacyTwoDwellings, PrivacyEnsuiteCount, PrivacyBathroomCount, PrivacyStaircaseCount,
        KidsBasement, KidsFloors, KidsStorage, KidsUtilityRoom,
        GardenStructure, GardenPrivateZones, GardenTerrace, GardenHasGarden,
        SharedOpenPlan, SharedDiningCapacity, SharedKitchenSize, SharedCondition,
        PracticalGarageOnPlan, PracticalGarageVisible
    }

    private static FamilyScoreInput CompleteInput() => new(
        new PrivacyScoreInput(EvidenceAssessmentStatus.Complete, true, true, false,
            DwellingSplitType.SideBySide, true, 2, 3, 2),
        new KidsSpaceScoreInput(EvidenceAssessmentStatus.Complete, 8, 220, true, 2, true, true),
        new GardenScoreInput(EvidenceAssessmentStatus.Complete, 1200, true, true, true, true),
        new SharedLivingScoreInput(EvidenceAssessmentStatus.Complete, true, 8, KitchenSize.Large,
            PropertyCondition.Excellent),
        new PracticalScoreInput(EvidenceAssessmentStatus.Complete, 2, true, true, true, 3,
            "A2020", CoveredNoise(45), CoveredNoise(67), CoveredNoise(51)),
        BrokerContradiction: false);

    private static NoiseObservation CoveredNoise(double db) =>
        new(NoiseEvidenceStatus.Covered, db);
}
