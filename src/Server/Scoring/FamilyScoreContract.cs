using HouseConsensus.Shared;

namespace HouseConsensus.Server.Scoring;

public enum EvidenceAssessmentStatus
{
    Complete,
    Unavailable,
    Failed,
    Unreadable
}

public enum DwellingSplitType
{
    Unknown,
    None,
    Horizontal,
    Vertical,
    SideBySide
}

public enum KitchenSize
{
    Unknown,
    Small,
    Medium,
    Large
}

public enum PropertyCondition
{
    Unknown,
    Poor,
    Fair,
    Good,
    Excellent
}

public enum NoiseEvidenceStatus
{
    Covered,
    NoContour,
    Unavailable,
    Error,
    Stale
}

public sealed record NoiseObservation(NoiseEvidenceStatus Status, double? Decibels);

public sealed record PrivacyScoreInput(
    EvidenceAssessmentStatus Status,
    bool? SeparateEntrance,
    bool? SecondKitchen,
    bool? InternalConnection,
    DwellingSplitType SplitType,
    bool? TwoDwellings,
    int? EnsuiteCount,
    int? BathroomCount,
    int? StaircaseCount);

public sealed record KidsSpaceScoreInput(
    EvidenceAssessmentStatus Status,
    int? Rooms,
    double? HousingAreaM2,
    bool? Basement,
    int? Floors,
    bool? Storage,
    bool? UtilityRoom);

public sealed record GardenScoreInput(
    EvidenceAssessmentStatus Status,
    double? LotAreaM2,
    bool? HasStructure,
    bool? PrivateZones,
    bool? Terrace,
    bool? HasGarden);

public sealed record SharedLivingScoreInput(
    EvidenceAssessmentStatus Status,
    bool? OpenPlan,
    int? DiningCapacity,
    KitchenSize KitchenSize,
    PropertyCondition Condition);

public sealed record PracticalScoreInput(
    EvidenceAssessmentStatus Status,
    int? ParkingCount,
    bool? GroundFloorBedroom,
    bool? GarageOnPlan,
    bool? GarageVisible,
    int? ToiletCount,
    string? EnergyLabel,
    NoiseObservation RoadNoise,
    NoiseObservation RailNoise,
    NoiseObservation AirNoise);

public sealed record FamilyScoreInput(
    PrivacyScoreInput Privacy,
    KidsSpaceScoreInput KidsSpace,
    GardenScoreInput Garden,
    SharedLivingScoreInput SharedLiving,
    PracticalScoreInput Practical,
    bool BrokerContradiction);

public sealed record ScoreDimensionResult(
    string Name,
    double? Score,
    int Weight,
    bool Available,
    IReadOnlyList<string> Notes);

public sealed record FamilyScoreContract(
    ScoreStatus Status,
    double? Total,
    ScoreDimensionResult Privacy,
    ScoreDimensionResult KidsSpace,
    ScoreDimensionResult Garden,
    ScoreDimensionResult SharedLiving,
    ScoreDimensionResult Practical,
    double CoveragePct,
    string RuleVersion,
    IReadOnlyList<string> BlockingReasons)
{
    public IReadOnlyList<ScoreDimensionResult> Dimensions =>
        [Privacy, KidsSpace, Garden, SharedLiving, Practical];
}
