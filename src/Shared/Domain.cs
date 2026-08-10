using System.Text.Json;
namespace HouseConsensus.Shared;

public enum MemberRole { Member, Owner }
public enum VoteChoice { Like, Dislike, NotVoted }
public enum ListingState { Active, FilterRejected, AiRejected, ManuallyRejected, Restored, Archived }
public enum ReasonTag { Layout, Privacy, Garden, Condition, Location, Noise, Price, Other, PrivacyFromNeighbors }
public enum VoteCategory { Layout, Privacy, Garden, Condition, Location, Noise, Price, MultiGenerationFit, Commute, Buildability }
public enum CategoryRating { Dislike = -1, Neutral = 0, Like = 1 }
public static class VoteCategories
{
    public static readonly VoteCategory[] All = [VoteCategory.Layout, VoteCategory.Privacy, VoteCategory.Garden, VoteCategory.Condition, VoteCategory.Location, VoteCategory.Noise, VoteCategory.Price, VoteCategory.MultiGenerationFit, VoteCategory.Commute, VoteCategory.Buildability];
}
public enum OverrideAction { Restore, Reject }
public enum AsbestosRoofStatus { Likely, Possible, NoIndication, Unknown }

public sealed class Member
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Email { get; set; }
    public string DisplayName { get; set; } = "";
    public string AvatarColor { get; private set; } = "";
    public string Language { get; set; } = "en";
    public MemberRole Role { get; set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public Guid VotingIdentityId { get; set; }
    public void Deactivate() => IsActive = false;
    public void Reactivate() => IsActive = true;
    public void SetLanguage(string language)
    {
        if (language is not ("en" or "da")) throw new DomainException("Language must be en or da.");
        Language = language;
    }
    public void SetProfile(string? displayName, string? avatarColor)
    {
        var nickname = displayName?.Trim() ?? "";
        if (nickname.Length is < 1 or > 40) throw new DomainException("Nickname must be between 1 and 40 characters.");
        if (!Shared.AvatarColor.IsValid(avatarColor)) throw new DomainException("Choose a supported avatar color.");
        DisplayName = nickname;
        AvatarColor = avatarColor!.ToLowerInvariant();
    }
}

public sealed class Listing
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ExternalId { get; set; }
    public required string Address { get; set; }
    public string? City { get; set; }
    public decimal? Price { get; set; }
    public double? FamilyFitScore { get; set; }
    public ListingState State { get; private set; } = ListingState.Active;
    public bool AiAssessed { get; set; }
    public double? AiConfidence { get; set; }
    public string? AiEvidence { get; set; }
    public string? ModelVersion { get; set; }
    public string? RuleVersion { get; set; }
    public string? LearningRuleVersion { get; private set; }
    public string? SourceUrl { get; set; }
    public string? CanonicalUrl { get; set; }
    public string? NormalizedAddress { get; set; }
    public bool IsManuallyAdded { get; private set; }
    public Guid? ManuallyAddedById { get; private set; }
    public DateTimeOffset? ManuallyAddedAt { get; private set; }
    public bool ManualLifecycleProtected { get; private set; }
    public DateTimeOffset? ManualScoringRequestedAt { get; set; }
    public DateTimeOffset? ManualScoringAttemptedAt { get; set; }
    public DateTimeOffset? ManualScoringCompletedAt { get; set; }
    public string? ManualScoringError { get; set; }
    public string? PreviewImageUrl { get; set; }
    public int? LivingArea { get; set; }
    public int? LotArea { get; set; }
    public int? Rooms { get; set; }
    public int? YearBuilt { get; set; }
    public int? Bathrooms { get; set; }
    public int? Bedrooms { get; set; }
    public int? Floors { get; set; }
    public string? EnergyLabel { get; set; }
    public bool? Quiet { get; set; }
    public double? RoadNoiseDb { get; set; }
    public string? RoadNoiseStatus { get; set; }
    public double? RoadNoiseLnightDb { get; set; }
    public string? RoadNoiseLnightStatus { get; set; }
    public double? RailNoiseDb { get; set; }
    public string? RailNoiseStatus { get; set; }
    public double? RailNoiseLnightDb { get; set; }
    public string? RailNoiseLnightStatus { get; set; }
    public double? AirNoiseDb { get; set; }
    public string? AirNoiseStatus { get; set; }
    public double? AirNoiseLnightDb { get; set; }
    public string? AirNoiseLnightStatus { get; set; }
    public int? BuildableHeadroom { get; set; }
    public bool? GroundFloorBedroom { get; set; }
    public bool? SeparateEntrance { get; set; }
    public bool? SecondKitchen { get; set; }
    public int? PrivacyScore { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? MonthlyExpense { get; set; }
    public int? DaysOnMarket { get; set; }
    public int? CommuteMinutes { get; set; }
    public string? CommuteJson { get; set; }
    public string? BuildableStatus { get; set; }
    public string? Condition { get; set; }
    public string? GardenOrientation { get; set; }
    public string? MultigenFit { get; set; }
    public string? PostalCode { get; set; }
    public bool? Preferred { get; set; }
    public bool? IsNew { get; set; }
    public DateTimeOffset? FirstSeenAt { get; set; }
    public string? FamilyUnits { get; set; }
    public double? FamilyPrivacyScore { get; set; }
    public double? KidsSpaceScore { get; set; }
    public double? GardenScore { get; set; }
    public double? SharedLivingScore { get; set; }
    public double? PracticalScore { get; set; }
    public double? FamilyPrivacyWeight { get; set; }
    public double? KidsSpaceWeight { get; set; }
    public double? GardenWeight { get; set; }
    public double? SharedLivingWeight { get; set; }
    public double? PracticalWeight { get; set; }
    public string? ScoreRuleVersion { get; set; }
    public double? ScoreCoveragePct { get; set; }
    public bool? FamilyPrivacyAvailable { get; set; }
    public string? ScoreNotesJson { get; set; }
    public AsbestosRoofStatus? AsbestosRoofCorrection { get; private set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; private set; }
    public List<ListingOverride> Overrides { get; } = [];
    public bool IsQueueEligible => State is ListingState.Active or ListingState.Restored;
    public static Listing CreateManual(string sourceUrl, string address, Guid memberId, DateTimeOffset at)
    {
        var canonical = ManualListing.NormalizeUrl(sourceUrl);
        var normalized = ManualListing.NormalizeAddress(address);
        return new Listing { ExternalId = $"manual:{Guid.NewGuid():N}", Address = address.Trim(), SourceUrl = canonical, CanonicalUrl = canonical, NormalizedAddress = normalized, IsManuallyAdded = true, ManuallyAddedById = memberId, ManuallyAddedAt = at, ManualLifecycleProtected = true, FamilyFitScore = null, ImportedAt = at };
    }
    public void ApplyOverride(OverrideAction action, Guid ownerId, string? reason, DateTimeOffset at)
    {
        Overrides.Add(new ListingOverride { ListingId = Id, OwnerId = ownerId, Action = action, Reason = reason, CreatedAt = at });
        State = action == OverrideAction.Restore ? ListingState.Restored : ListingState.ManuallyRejected;
        LearningRuleVersion = null;
        ArchivedAt = null;
    }
    public void ApplyImportDecision(bool aiRejected)
    {
        if (Overrides.Count != 0 || ManualLifecycleProtected) return;
        State = aiRejected ? ListingState.AiRejected : ListingState.Active;
    }
    public void ApplyLearningRejection(string version) => ApplyLearningDecision(version, true);
    public void ApplyLearningDecision(string version, bool rejected)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new DomainException("Rule version is required.");
        if (ManualLifecycleProtected) return;
        State = rejected ? ListingState.AiRejected : ListingState.Active;
        LearningRuleVersion = version;
        ArchivedAt = null;
    }
    public void RestoreLearningBaseline(string version, ListingState previousState, string? previousVersion = null)
    {
        if (!string.Equals(LearningRuleVersion, version, StringComparison.Ordinal)) return;
        if (previousState is not (ListingState.Active or ListingState.Restored or ListingState.AiRejected)) throw new DomainException("Invalid learning baseline state.");
        State = previousState;
        LearningRuleVersion = previousVersion;
    }
    public void ClearLearningRejection() { if (LearningRuleVersion is not null && Overrides.Count == 0) State = ListingState.Active; LearningRuleVersion = null; }
    public void SetAsbestosRoofCorrection(AsbestosRoofStatus? status) => AsbestosRoofCorrection = status;
    public void Archive(DateTimeOffset at, bool automated = false) { if (automated && ManualLifecycleProtected) return; State = ListingState.Archived; ArchivedAt = at; }
}

public sealed class ListingOverride
{
    public long Id { get; init; }
    public Guid ListingId { get; init; }
    public Guid OwnerId { get; init; }
    public OverrideAction Action { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class AsbestosRoofAssessment
{
    public long Id { get; init; }
    public Guid ListingId { get; init; }
    public string? RunId { get; init; }
    public required string Status { get; init; }
    public double? Confidence { get; init; }
    public string? PrimarySource { get; init; }
    public required string EvidenceJson { get; init; }
    public required string RuleVersion { get; init; }
    public required string SourceFingerprint { get; init; }
    public DateTimeOffset AssessedAt { get; init; }
}

public sealed class Vote
{
    public long Id { get; init; }
    public Guid ListingId { get; init; }
    public Guid MemberId { get; init; }
    public VoteChoice Choice { get; init; }
    public ReasonTag[] Tags { get; init; } = [];
    public string? Note { get; private set; }
    public int? OverallScore { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<VoteRating> Ratings { get; } = [];
    public List<VoteNoteRevision> NoteRevisions { get; } = [];
    public int Total => Ratings.Sum(x => (int)x.Rating);
    public Vote() { }
    public Vote(Guid listingId, Guid memberId, IEnumerable<VoteRating> ratings, int overallScore, string? note, DateTimeOffset at)
    {
        if (overallScore is < 1 or > 5) throw new DomainException("Overall score must be from 1 to 5.");
        var values = ratings.ToArray();
        if (values.Length != VoteCategories.All.Length || values.Select(x => x.Category).Distinct().Count() != VoteCategories.All.Length || VoteCategories.All.Any(x => values.All(r => r.Category != x))) throw new DomainException("All ten voting categories are required exactly once.");
        if (values.Any(x => !Enum.IsDefined(x.Rating))) throw new DomainException("Each category rating must be Dislike, Neutral, or Like.");
        if (values.All(x => x.Rating == CategoryRating.Neutral)) throw new DomainException("Rate at least one category.");
        ListingId = listingId; MemberId = memberId; OverallScore = overallScore; Note = NormalizeNote(note); CreatedAt = at; Ratings.AddRange(values.Select(x => new VoteRating { Category = x.Category, Rating = x.Rating })); Choice = Total > 0 ? VoteChoice.Like : VoteChoice.Dislike;
    }
    public Vote(Guid listingId, Guid memberId, VoteChoice choice, ReasonTag[] tags, string? note, DateTimeOffset at)
    { ListingId = listingId; MemberId = memberId; Choice = choice; Tags = tags.Distinct().ToArray(); Note = NormalizeNote(note); CreatedAt = at; }
    public void EditNote(Guid actorId, string? note, DateTimeOffset at)
    {
        if (actorId != MemberId) throw new DomainException("Only the voter may edit their vote note.");
        var normalized = NormalizeNote(note); if (normalized == Note) return; NoteRevisions.Add(new VoteNoteRevision { VoteId = Id, PreviousNote = Note, ActorId = actorId, ChangedAt = at }); Note = normalized;
    }
    private static string? NormalizeNote(string? note) { var value = string.IsNullOrWhiteSpace(note) ? null : note.Trim(); return value?.Length > 2000 ? throw new DomainException("Vote note must be at most 2000 characters.") : value; }
}
public sealed class VoteRating
{
    public long Id { get; init; }
    public long VoteId { get; init; }
    public VoteCategory Category { get; set; }
    public CategoryRating Rating { get; set; }
}
public sealed class VoteNoteRevision
{
    public long Id { get; init; }
    public long VoteId { get; init; }
    public string? PreviousNote { get; init; }
    public Guid ActorId { get; init; }
    public DateTimeOffset ChangedAt { get; init; }
}

public sealed class Comment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ListingId { get; init; }
    public Guid AuthorId { get; init; }
    public string Body { get; private set; } = "";
    public bool IsDeleted { get; private set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public List<CommentRevision> Revisions { get; } = [];
    public Comment() { }
    public Comment(Guid listingId, Guid authorId, string body, DateTimeOffset at)
    { ListingId = listingId; AuthorId = authorId; Body = Validate(body); CreatedAt = UpdatedAt = at; }
    public void Edit(Guid actorId, bool owner, string body, DateTimeOffset at)
    {
        if (IsDeleted) throw new DomainException("Deleted comments cannot be edited.");
        if (actorId != AuthorId && !owner) throw new DomainException("Only the author or owner may edit.");
        Revisions.Add(new CommentRevision { CommentId = Id, PreviousBody = Body, ActorId = actorId, ChangedAt = at, WasDeletion = false });
        Body = Validate(body); UpdatedAt = at;
    }
    public void Delete(Guid actorId, bool owner, DateTimeOffset at)
    {
        if (actorId != AuthorId && !owner) throw new DomainException("Only the author or owner may delete.");
        if (IsDeleted) return;
        Revisions.Add(new CommentRevision { CommentId = Id, PreviousBody = Body, ActorId = actorId, ChangedAt = at, WasDeletion = true });
        IsDeleted = true; Body = ""; UpdatedAt = at;
    }
    private static string Validate(string body) => string.IsNullOrWhiteSpace(body) || body.Length > 4000 ? throw new DomainException("Comment must be 1-4000 characters.") : body.Trim();
}
public sealed class CommentRevision
{
    public long Id { get; init; }
    public Guid CommentId { get; init; }
    public required string PreviousBody { get; init; }
    public Guid ActorId { get; init; }
    public bool WasDeletion { get; init; }
    public DateTimeOffset ChangedAt { get; init; }
}
public sealed class AiRuleProposal
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid CreatedById { get; init; }
    public int Version { get; init; }
    public string VersionLabel => $"feedback-v{Version}";
    public string Summary { get; private set; } = "";
    public string RuleJson { get; private set; } = "{}";
    public string SupportingNotesJson { get; private set; } = "[]";
    public string ImpactPreviewJson { get; private set; } = "{}";
    public string Status { get; private set; } = "draft";
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? ReviewedById { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public Guid? PreviousProposalId { get; private set; }
    public AiRuleProposal() { }
    public AiRuleProposal(Guid createdById, int version, string summary, string ruleJson, string supportingNotesJson, string impactPreviewJson, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(summary)) throw new DomainException("Proposal summary is required.");
        AiLearningRules.Validate(ruleJson);
        CreatedById = createdById; Version = version; Summary = summary.Trim(); RuleJson = ruleJson;
        SupportingNotesJson = supportingNotesJson; ImpactPreviewJson = impactPreviewJson; CreatedAt = at;
    }
    public void Approve(Guid ownerId, DateTimeOffset at, Guid? previousProposalId = null) { if (Status != "draft") throw new DomainException("Only draft proposals can be approved."); Status = "approved"; IsActive = true; ReviewedById = ownerId; ReviewedAt = at; PreviousProposalId = previousProposalId; }
    public void Reject(Guid ownerId, DateTimeOffset at) { if (Status != "draft") throw new DomainException("Only draft proposals can be rejected."); Status = "rejected"; IsActive = false; ReviewedById = ownerId; ReviewedAt = at; }
    public void Deactivate() { IsActive = false; }
    public void Reactivate() { if (Status != "approved") throw new DomainException("Only approved proposals can be reactivated."); IsActive = true; }
}

public sealed class AiRuleProposalAction
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ProposalId { get; init; }
    public required string Action { get; init; }
    public Guid ActorId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class AiRuleApplication
{
    public long Id { get; init; }
    public Guid ProposalId { get; init; }
    public Guid ListingId { get; init; }
    public required string ListingExternalId { get; init; }
    public ListingState PreviousState { get; init; }
    public string? PreviousLearningRuleVersion { get; init; }
    public ListingState AppliedState { get; init; }
    public DateTimeOffset AppliedAt { get; init; }
}

public sealed class Feedback
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid MemberId { get; init; }
    public Guid? ListingId { get; init; }
    public required string Body { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }
}
public sealed class Invite
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Email { get; init; }
    public Guid InvitedById { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public bool IsValid(DateTimeOffset now) => AcceptedAt is null && ExpiresAt > now;
}
public sealed class MagicLink
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Email { get; init; }
    public required string TokenHash { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public bool IsValid(DateTimeOffset now) => ConsumedAt is null && ExpiresAt > now;
}
public static class ConsensusRules
{
    public static bool HasConsensus(IEnumerable<Guid> activeMemberIds, IEnumerable<Vote> votes)
    {
        var members = activeMemberIds.Distinct().ToArray();
        if (members.Length == 0) return false;
        var latest = votes.GroupBy(v => v.MemberId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id).First().Choice);
        return members.All(id => latest.TryGetValue(id, out var choice) && choice == VoteChoice.Like);
    }
    public static IReadOnlyDictionary<Guid, Vote> LatestVotes(IEnumerable<Vote> votes) => votes.GroupBy(v => v.MemberId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id).First());
    public static bool HasConsensus(IEnumerable<Guid> activeVotingIdentityIds, IEnumerable<Vote> votes, IReadOnlyDictionary<Guid, Guid> memberIdentities)
    {
        var identities = activeVotingIdentityIds.Distinct().ToArray();
        if (identities.Length == 0) return false;
        var latest = LatestVotes(votes, memberIdentities);
        return identities.All(id => latest.TryGetValue(id, out var vote) && vote.Choice == VoteChoice.Like);
    }
    public static IReadOnlyDictionary<Guid, Vote> LatestVotes(IEnumerable<Vote> votes, IReadOnlyDictionary<Guid, Guid> memberIdentities) => votes
        .GroupBy(v => memberIdentities.GetValueOrDefault(v.MemberId, v.MemberId))
        .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id).First());
}
public static class AiLearningRules
{
    private static readonly HashSet<string> Fields = ["condition", "multigenfit", "multigen_fit", "buildablestatus", "buildable_status", "gardenorientation", "garden_orientation", "energylabel", "energy_label", "privacyscore", "privacy_score", "familyscore", "family_score", "separateentrance", "separate_entrance", "secondkitchen", "second_kitchen", "groundfloorbedroom", "ground_floor_bedroom"];
    private static readonly HashSet<string> Operators = ["eq", "neq", "contains", "lt", "lte", "gt", "gte"];
    private static readonly HashSet<string> NumericFields = ["privacyscore", "privacy_score", "familyscore", "family_score"];
    private static readonly HashSet<string> BooleanFields = ["separateentrance", "separate_entrance", "secondkitchen", "second_kitchen", "groundfloorbedroom", "ground_floor_bedroom"];
    public static void Validate(string ruleJson)
    {
        using var document = JsonDocument.Parse(ruleJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new DomainException("Rule must be an object.");
        var combinator = root.TryGetProperty("combinator", out var combination) ? combination.GetString()?.ToLowerInvariant() : "all";
        if (combinator is not ("all" or "any")) throw new DomainException("Rule combinator must be all or any.");
        if (!root.TryGetProperty("conditions", out var conditions) || conditions.ValueKind != JsonValueKind.Array || conditions.GetArrayLength() == 0) throw new DomainException("Rule requires conditions.");
        if (conditions.GetArrayLength() > 10) throw new DomainException("Rule has too many conditions.");
        foreach (var condition in conditions.EnumerateArray())
        {
            if (condition.ValueKind != JsonValueKind.Object || !condition.TryGetProperty("field", out var fieldValue) || fieldValue.ValueKind != JsonValueKind.String || !condition.TryGetProperty("operator", out var operatorValue) || operatorValue.ValueKind != JsonValueKind.String || !condition.TryGetProperty("value", out var value)) throw new DomainException("Rule contains a malformed condition.");
            var field = fieldValue.GetString()?.ToLowerInvariant();
            var op = operatorValue.GetString()?.ToLowerInvariant();
            if (field is null || !Fields.Contains(field) || op is null || !Operators.Contains(op)) throw new DomainException("Rule contains an unsupported condition.");
            if (NumericFields.Contains(field) && (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || op == "contains")) throw new DomainException("Numeric rule condition has an invalid operator or value.");
            if (BooleanFields.Contains(field) && (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || op is not ("eq" or "neq"))) throw new DomainException("Boolean rule condition has an invalid operator or value.");
            if (!NumericFields.Contains(field) && !BooleanFields.Contains(field) && (value.ValueKind != JsonValueKind.String || op is not ("eq" or "neq" or "contains"))) throw new DomainException("Text rule condition has an invalid operator or value.");
        }
    }
    public static bool Apply(Listing listing, bool hasVote, string version, string ruleJson)
    {
        if (hasVote || listing.Overrides.Count != 0 || listing.AiConfidence is null or < .999 || !Matches(listing, ruleJson)) return false;
        listing.ApplyLearningRejection(version);
        return true;
    }

    public static bool Matches(Listing listing, string ruleJson)
    {
        using var document = JsonDocument.Parse(ruleJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("conditions", out var conditions) || conditions.ValueKind != JsonValueKind.Array || conditions.GetArrayLength() == 0) return false;
        var matches = conditions.EnumerateArray().Select(x => MatchesCondition(listing, x)).ToArray();
        var combinator = root.TryGetProperty("combinator", out var c) ? c.GetString() : "all";
        return string.Equals(combinator, "any", StringComparison.OrdinalIgnoreCase) ? matches.Any(x => x) : matches.All(x => x);
    }

    private static double? TrustedFamilyScore(Listing listing)
    {
        var status = ScoreStatusRules.Resolve(
            listing.FamilyFitScore,
            listing.AiAssessed,
            listing.ScoreCoveragePct,
            listing.FamilyPrivacyAvailable,
            listing.FamilyPrivacyScore.HasValue && listing.KidsSpaceScore.HasValue
                && listing.GardenScore.HasValue && listing.SharedLivingScore.HasValue
                && listing.PracticalScore.HasValue && listing.FamilyPrivacyWeight.HasValue
                && listing.KidsSpaceWeight.HasValue && listing.GardenWeight.HasValue
                && listing.SharedLivingWeight.HasValue && listing.PracticalWeight.HasValue,
            listing.ScoreRuleVersion);
        return status == ScoreStatus.Complete ? listing.FamilyFitScore : null;
    }

    private static bool MatchesCondition(Listing listing, JsonElement condition)
    {
        var field = condition.GetProperty("field").GetString()?.ToLowerInvariant();
        var op = condition.GetProperty("operator").GetString()?.ToLowerInvariant();
        var expected = condition.GetProperty("value");
        object? actual = field switch
        {
            "condition" => listing.Condition, "multigenfit" or "multigen_fit" => listing.MultigenFit,
            "buildablestatus" or "buildable_status" => listing.BuildableStatus,
            "gardenorientation" or "garden_orientation" => listing.GardenOrientation,
            "energylabel" or "energy_label" => listing.EnergyLabel,
            "privacyscore" or "privacy_score" => listing.PrivacyScore,
            "familyscore" or "family_score" => TrustedFamilyScore(listing),
            "separateentrance" or "separate_entrance" => listing.SeparateEntrance,
            "secondkitchen" or "second_kitchen" => listing.SecondKitchen,
            "groundfloorbedroom" or "ground_floor_bedroom" => listing.GroundFloorBedroom,
            _ => null,
        };
        if (actual is null) return false;
        if (actual is string text && expected.ValueKind == JsonValueKind.String)
        {
            var value = expected.GetString() ?? "";
            return op switch { "eq" => text.Equals(value, StringComparison.OrdinalIgnoreCase), "neq" => !text.Equals(value, StringComparison.OrdinalIgnoreCase), "contains" => text.Contains(value, StringComparison.OrdinalIgnoreCase), _ => false };
        }
        if (actual is bool boolean && expected.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return op switch { "eq" => boolean == expected.GetBoolean(), "neq" => boolean != expected.GetBoolean(), _ => false };
        if (expected.ValueKind == JsonValueKind.Number && double.TryParse(Convert.ToString(actual, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            var value = expected.GetDouble();
            return op switch { "eq" => Math.Abs(number - value) < .0001, "neq" => Math.Abs(number - value) >= .0001, "lt" => number < value, "lte" => number <= value, "gt" => number > value, "gte" => number >= value, _ => false };
        }
        return false;
    }
}

public static class ManualListing
{
    public const int MaxCanonicalUrlLength = 2048;
    public const int MaxNormalizedAddressLength = 500;
    public const decimal MaxAskingPrice = 999_999_999_999.99m;
    private static readonly HashSet<string> Tracking = new(StringComparer.OrdinalIgnoreCase) { "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "gclid", "fbclid" };
    public static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) throw new DomainException("A valid HTTPS URL is required.");
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query); foreach (var key in query.AllKeys.Where(x => x is not null && Tracking.Contains(x!)).ToArray()) query.Remove(key);
        var builder = new UriBuilder(uri) { Scheme = "https", Host = uri.IdnHost.ToLowerInvariant(), Port = uri.IsDefaultPort ? -1 : uri.Port, Fragment = "", Query = query.ToString() ?? "", Path = uri.AbsolutePath == "/" ? "/" : uri.AbsolutePath.TrimEnd('/') };
        var normalized = builder.Uri.AbsoluteUri.TrimEnd('?');
        if (normalized.Length > MaxCanonicalUrlLength) throw new DomainException($"Listing URL must normalize to at most {MaxCanonicalUrlLength} characters.");
        return normalized;
    }
    public static string NormalizeAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException("Address is required.");
        var display = value.Trim();
        if (display.Length > MaxNormalizedAddressLength) throw new DomainException($"Address must be at most {MaxNormalizedAddressLength} characters.");
        var normalized = System.Text.RegularExpressions.Regex.Replace(display.ToLowerInvariant(), @"\s+", " ").Replace(" ,", ",");
        if (normalized.Length > MaxNormalizedAddressLength) throw new DomainException($"Address must normalize to at most {MaxNormalizedAddressLength} characters.");
        return normalized;
    }
}

public sealed class DomainException(string message) : InvalidOperationException(message);

