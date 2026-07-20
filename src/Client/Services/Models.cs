using HouseConsensus.Shared;

namespace HouseConsensus.Client.Services;

public sealed record CommentDto(Guid Id, Guid AuthorId, string Body, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record FeedbackDto(Guid Id, Guid MemberId, Guid? ListingId, string Body, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt);
public sealed record VoteResult(VoteDto Vote, bool Consensus);
