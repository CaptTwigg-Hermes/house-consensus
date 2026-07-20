using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
namespace HouseConsensus.Server.Hubs;

[Authorize]
public sealed class ConsensusHub : Hub
{
    public Task WatchListing(Guid listingId) => Groups.AddToGroupAsync(Context.ConnectionId, $"listing:{listingId}");
    public Task LeaveListing(Guid listingId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"listing:{listingId}");
}

