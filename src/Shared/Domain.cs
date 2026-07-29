using System.Text.Json;
namespace HouseConsensus.Shared;

public enum MemberRole { Member, Owner }
public enum VoteChoice { Like, Dislike, NotVoted }
public enum ListingState { Active, FilterRejected, AiRejected, ManuallyRejected, Restored, Archived }
public enum ReasonTag { Layout, Privacy, Garden, Condition, Location, Noise, Price, Other, PrivacyFromNeighbors }
public enum OverrideAction { Restore, Reject }

public sealed class Member
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Email { get; set; }
    public string DisplayName { get; set; } = "";
    public string Language { get; set; } = "en";
    public MemberRole Role { get; set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public void Deactivate() => IsActive = false;
    public void Reactivate() => IsActive = true;
    public void SetLanguage(string language)
    {
        if (language is not ("en" or "da")) throw new DomainException("Language must be en or da.");
        Language = language;
    }
}

public sealed class Listing
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ExternalId { get; set; }
    public required string Address { get; set; }
    public string? City { get; set; }
    public decimal? Price { get; set; }
    public double FamilyFitScore { get; set; }
    public ListingState State { get; private set; } = ListingState.Active;
    public bool AiAssessed { get; set; }
    public double? AiConfidence { get; set; }
    public string? AiEvidence { get; set; }
    public string? ModelVersion { get; set; }
    public string? RuleVersion { get; set; }
    public string? LearningRuleVersion { get; private set; }
    public string? SourceUrl { get; set; }
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
    public double? RailNoiseDb { get; set; }
    public double? AirNoiseDb { get; set; }
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
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; private set; }
    public List<ListingOverride> Overrides { get; } = [];
    public bool IsQueueEligible => State is ListingState.Active or ListingState.Restored;
    public void ApplyOverride(OverrideAction action, Guid ownerId, string? reason, DateTimeOffset at)
    {
        Overrides.Add(new ListingOverride { ListingId = Id, OwnerId = ownerId, Action = action, Reason = reason, CreatedAt = at });
        State = action == OverrideAction.Restore ? ListingState.Restored : ListingState.ManuallyRejected;
        LearningRuleVersion = null;
        ArchivedAt = null;
    }
    public void ApplyImportDecision(bool aiRejected)
    {
        if (Overrides.Count != 0) return;
        State = aiRejected ? ListingState.AiRejected : ListingState.Active;
    }
    public void ApplyLearningRejection(string version) => ApplyLearningDecision(version, true);
    public void ApplyLearningDecision(string version, bool rejected)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new DomainException("Rule version is required.");
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
    public void Archive(DateTimeOffset at) { State = ListingState.Archived; ArchivedAt = at; }
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

public sealed class Vote
{
    public long Id { get; init; }
    public Guid ListingId { get; init; }
    public Guid MemberId { get; init; }
    public VoteChoice Choice { get; init; }
    public ReasonTag[] Tags { get; init; } = [];
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<VoteNoteRevision> NoteRevisions { get; } = [];
    public Vote() { }
    public Vote(Guid listingId, Guid memberId, VoteChoice choice, ReasonTag[] tags, string? note, DateTimeOffset at)
    {
        ListingId = listingId; MemberId = memberId; Choice = choice;
        Tags = tags.Distinct().ToArray(); Note = NormalizeNote(note); CreatedAt = at;
    }
    public void EditNote(Guid actorId, string? note, DateTimeOffset at)
    {
        if (actorId != MemberId) throw new DomainException("Only the voter may edit their vote note.");
        var normalized = NormalizeNote(note);
        if (normalized == Note) return;
        NoteRevisions.Add(new VoteNoteRevision { VoteId = Id, PreviousNote = Note, ActorId = actorId, ChangedAt = at });
        Note = normalized;
    }
    private static string? NormalizeNote(string? note)
    {
        var value = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return value?.Length > 2000 ? throw new DomainException("Vote note must be at most 2000 characters.") : value;
    }
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
            "familyscore" or "family_score" => listing.FamilyFitScore,
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

public sealed class DomainException(string message) : InvalidOperationException(message);

