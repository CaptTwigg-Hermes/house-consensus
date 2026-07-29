namespace HouseConsensus.Shared;

public sealed record RequestMagicLink(string Email);
public sealed record ConsumeMagicLink(string Token);
public sealed record CreateInvite(string Email);
public sealed record CastVote(VoteChoice Choice, ReasonTag[]? Tags, string? Note = null);
public sealed record EditVoteNote(string? Note);
public sealed record AddComment(string Body);
public sealed record EditComment(string Body);
public sealed record SubmitFeedback(Guid? ListingId, string Body);
public sealed record ReviewFeedback(bool Reviewed);
public sealed record ApplyListingOverride(OverrideAction Action, string? Reason);
public sealed record UpdateLanguage(string Language);
public sealed record AuthModeDto(bool CloudflareAccess);
public sealed record MemberDto(Guid Id, string Email, string DisplayName, string Language, MemberRole Role, bool IsActive);
public sealed record VoteDto(long Id, Guid ListingId, Guid MemberId, VoteChoice Choice, ReasonTag[] Tags, DateTimeOffset CreatedAt, string? Note = null, string MemberInitials = "");
public sealed record ListingDto(
    Guid Id, string ExternalId, string Address, string? City, decimal? Price,
    double FamilyFitScore, ListingState State, bool AiAssessed, double? AiConfidence,
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
    double? RoadNoiseDb = null, double? RailNoiseDb = null, double? AirNoiseDb = null);


public sealed record AiRuleImpactDto(int Eligible, int Evaluated, int WouldReject, int WouldRestore, int Changed, Guid[] ListingIds);
public sealed record AiRuleSourceNoteDto(long VoteId, Guid ListingId, Guid MemberId, VoteChoice Choice, ReasonTag[] Tags, string Note);
public sealed record AiRuleProposalDto(Guid Id, int Version, string VersionLabel, string Summary, string RuleJson, AiRuleImpactDto Impact, IReadOnlyList<AiRuleSourceNoteDto> SourceNotes, string Status, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt);
