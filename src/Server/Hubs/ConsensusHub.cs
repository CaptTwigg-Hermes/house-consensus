using HouseConsensus.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace HouseConsensus.Server.Hubs;

[Authorize]
public sealed class ConsensusHub(ILogger<ConsensusHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        logger.LogInformation(
            new EventId(DiagnosticEventIds.SignalRLifecycle, nameof(DiagnosticEventIds.SignalRLifecycle)),
            "SignalR client connected: connection {ConnectionId}, member {MemberId}",
            Context.ConnectionId,
            Context.UserIdentifier);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.Log(
            exception is null ? LogLevel.Information : LogLevel.Warning,
            new EventId(DiagnosticEventIds.SignalRLifecycle, nameof(DiagnosticEventIds.SignalRLifecycle)),
            "SignalR client disconnected: connection {ConnectionId}, member {MemberId}, failure {FailureType}",
            Context.ConnectionId,
            Context.UserIdentifier,
            exception?.GetType().Name);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task WatchListing(Guid listingId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(listingId));
        logger.LogDebug(
            new EventId(DiagnosticEventIds.SignalRLifecycle, nameof(DiagnosticEventIds.SignalRLifecycle)),
            "SignalR connection {ConnectionId} is watching listing {ListingId}",
            Context.ConnectionId,
            listingId);
    }

    public async Task LeaveListing(Guid listingId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(listingId));
        logger.LogDebug(
            new EventId(DiagnosticEventIds.SignalRLifecycle, nameof(DiagnosticEventIds.SignalRLifecycle)),
            "SignalR connection {ConnectionId} stopped watching listing {ListingId}",
            Context.ConnectionId,
            listingId);
    }

    public static string Group(Guid listingId) => $"listing:{listingId}";
}
