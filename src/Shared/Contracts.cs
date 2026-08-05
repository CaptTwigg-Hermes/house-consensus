namespace HouseConsensus.Shared;

public sealed record RequestMagicLink(string Email);
public sealed record ConsumeMagicLink(string Token);
public sealed record CastVote(IReadOnlyCollection<VoteRatingInput>? Ratings, int OverallScore, string? Note = null, VoteChoice? Choice = null);
public sealed record VoteRatingInput(VoteCategory Category, CategoryRating Rating);
public sealed record VoteRatingDto(VoteCategory Category, CategoryRating Rating);
public sealed record CreateManualListing(string Url, string Address, string? City = null, decimal? AskingPrice = null);
public sealed record FetchManualListing(string Url);
public sealed record ManualListingPreview(
    string Address, string? City, string? PostalCode, decimal? AskingPrice,
    int? LivingArea, int? LotArea, int? Rooms, int? Floors, int? Bathrooms, int? YearBuilt,
    string? EnergyLabel, int? MonthlyExpense, int? DaysOnMarket, string? PreviewImageUrl,
    double? Latitude, double? Longitude, string ExternalId);
public sealed record ManualListingResult(Guid ListingId, bool Existing);
public sealed record EditVoteNote(string? Note);
public sealed record AddComment(string Body);
public sealed record EditComment(string Body);
public sealed record SubmitFeedback(Guid? ListingId, string Body);
public sealed record ReviewFeedback(bool Reviewed);
public sealed record ApplyListingOverride(OverrideAction Action, string? Reason);
public sealed record UpdateLanguage(string Language);
public sealed record UpdateProfile(string DisplayName, string AvatarColor);
public sealed record AuthModeDto(bool CloudflareAccess);
public sealed record BuildVersionDto(string Version);
public sealed record MemberDto(Guid Id, string Email, string DisplayName, string Language, MemberRole Role, bool IsActive, string AvatarColor = "");
public sealed record VoteDto(long Id, Guid ListingId, Guid MemberId, VoteChoice Choice, ReasonTag[] Tags, DateTimeOffset CreatedAt, string? Note = null, string MemberInitials = "", string MemberColor = "", IReadOnlyCollection<VoteRatingDto>? Ratings = null, int Total = 0, int? OverallScore = null);
public sealed record ListingDto(
    Guid Id, string ExternalId, string Address, string? City, decimal? Price,
    double? FamilyFitScore, ListingState State, bool AiAssessed, double? AiConfidence,
    string? AiEvidence, string? ModelVersion, string? RuleVersion, string? SourceUrl,
    bool Consensus, IReadOnlyCollection<VoteDto> Votes,
    string? PreviewImageUrl = null, int? LivingArea = null, int? LotArea = null,
    int? Rooms = null, int? YearBuilt = null, int? Bathrooms = null,
    int? Bedrooms = null, int? Floors = null, string? EnergyLabel = null,
    bool? Quiet = null, int? BuildableHeadroom = null,
    bool? GroundFloorBedroom = null, bool? SeparateEntrance = null,
    bool? SecondKitchen = null, int? PrivacyScore = null,
    double? FamilyPrivacyScore = null, double? KidsSpaceScore = null,
    double? GardenScore = null, double? SharedLivingScore = null,
    double? PracticalScore = null, double? FamilyPrivacyWeight = null,
    double? KidsSpaceWeight = null, double? GardenWeight = null,
    double? SharedLivingWeight = null, double? PracticalWeight = null,
    double? Latitude = null, double? Longitude = null, int? MonthlyExpense = null,
    int? DaysOnMarket = null, int? CommuteMinutes = null, string? BuildableStatus = null,
    string? Condition = null, string? GardenOrientation = null, string? MultigenFit = null,
    DateTimeOffset? ImportedAt = null, string? PostalCode = null, bool? Preferred = null,
    bool? IsNew = null, string? FamilyUnits = null, string? CommuteJson = null, DateTimeOffset? FirstSeenAt = null,
    double? RoadNoiseDb = null, double? RailNoiseDb = null, double? AirNoiseDb = null,
    bool IsManuallyAdded = false, Guid? ManuallyAddedById = null, string? ManuallyAddedByName = null, DateTimeOffset? ManuallyAddedAt = null, bool CanWithdraw = false, bool CanArchive = false,
    string? RoadNoiseStatus = null, double? RoadNoiseLnightDb = null, string? RoadNoiseLnightStatus = null,
    string? RailNoiseStatus = null, double? RailNoiseLnightDb = null, string? RailNoiseLnightStatus = null,
    string? AirNoiseStatus = null, double? AirNoiseLnightDb = null, string? AirNoiseLnightStatus = null,
    double? ScoreCoveragePct = null, bool? FamilyPrivacyAvailable = null,
    string? ScoreRuleVersion = null, string? ScoreNotesJson = null)
{
    public ScoreStatus ScoreStatus => ScoreStatusRules.Resolve(
        FamilyFitScore,
        AiAssessed,
        ScoreCoveragePct,
        FamilyPrivacyAvailable,
        FamilyPrivacyScore.HasValue && KidsSpaceScore.HasValue && GardenScore.HasValue
            && SharedLivingScore.HasValue && PracticalScore.HasValue
            && FamilyPrivacyWeight.HasValue && KidsSpaceWeight.HasValue && GardenWeight.HasValue
            && SharedLivingWeight.HasValue && PracticalWeight.HasValue,
        ScoreRuleVersion);

    public double? TrustedFamilyFitScore => ScoreStatus == ScoreStatus.Complete ? FamilyFitScore : null;

    public string ScoreStatusLabelKey => ScoreStatus switch
    {
        ScoreStatus.Incomplete => "ScoreIncomplete",
        ScoreStatus.NeedsReview => "ScoreNeedsReview",
        ScoreStatus.NotScored => "ScoreNotScored",
        _ => "FamilyFit"
    };
}


public sealed record AiRuleImpactDto(int Eligible, int Evaluated, int WouldReject, int WouldRestore, int Changed, Guid[] ListingIds);
public sealed record AiRuleSourceNoteDto(long VoteId, Guid ListingId, Guid MemberId, VoteChoice Choice, ReasonTag[] Tags, string Note);
public sealed record AiRuleProposalDto(Guid Id, int Version, string VersionLabel, string Summary, string RuleJson, AiRuleImpactDto Impact, IReadOnlyList<AiRuleSourceNoteDto> SourceNotes, string Status, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt);
