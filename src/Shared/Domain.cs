namespace HouseConsensus.Shared;

public enum MemberRole { Member, Owner }
public enum VoteChoice { Like, Dislike, NotVoted }
public enum ListingState { Active, FilterRejected, AiRejected, ManuallyRejected, Restored, Archived }
public enum ReasonTag { Layout, Privacy, Garden, Condition, Location, Noise, Price, Other }
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
    public int? BuildableHeadroom { get; set; }
    public bool? GroundFloorBedroom { get; set; }
    public bool? SeparateEntrance { get; set; }
    public bool? SecondKitchen { get; set; }
    public int? PrivacyScore { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; private set; }
    public List<ListingOverride> Overrides { get; } = [];
    public bool IsQueueEligible => State is ListingState.Active or ListingState.Restored;
    public void ApplyOverride(OverrideAction action, Guid ownerId, string? reason, DateTimeOffset at)
    {
        Overrides.Add(new ListingOverride { ListingId = Id, OwnerId = ownerId, Action = action, Reason = reason, CreatedAt = at });
        State = action == OverrideAction.Restore ? ListingState.Restored : ListingState.ManuallyRejected;
        ArchivedAt = null;
    }
    public void ApplyImportDecision(bool aiRejected)
    {
        if (Overrides.Count != 0) return;
        State = aiRejected ? ListingState.AiRejected : ListingState.Active;
    }
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
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
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
public sealed class DomainException(string message) : InvalidOperationException(message);

