namespace HouseConsensus.Shared;

public sealed record RequestMagicLink(string Email);
public sealed record ConsumeMagicLink(string Token);
public sealed record CreateInvite(string Email);
public sealed record CastVote(VoteChoice Choice, ReasonTag[]? Tags);
public sealed record AddComment(string Body);
public sealed record EditComment(string Body);
public sealed record SubmitFeedback(Guid? ListingId, string Body);
public sealed record ReviewFeedback(bool Reviewed);
public sealed record ApplyListingOverride(OverrideAction Action, string? Reason);
public sealed record UpdateLanguage(string Language);
public sealed record MemberDto(Guid Id, string Email, string DisplayName, string Language, MemberRole Role, bool IsActive);
public sealed record VoteDto(long Id, Guid ListingId, Guid MemberId, VoteChoice Choice, ReasonTag[] Tags, DateTimeOffset CreatedAt);
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
    bool? SecondKitchen = null, int? PrivacyScore = null);

