using HouseConsensus.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace HouseConsensus.Client.Services;

public sealed class LiveUpdates(NavigationManager navigation, AuthState auth, ClientDiagnostics? diagnostics = null) : IAsyncDisposable
{
    private HubConnection? hub;
    public event Action? Changed;
    public bool Connected => hub?.State == HubConnectionState.Connected;

    public async Task StartAsync()
    {
        if (hub is not null) return;
        hub = new HubConnectionBuilder().WithUrl(navigation.ToAbsoluteUri("hubs/consensus")).WithAutomaticReconnect().Build();
        hub.On<Guid, bool>("ConsensusChanged", (_, _) => Changed?.Invoke());
        hub.On<Guid, ListingState>("ListingStateChanged", (_, _) => Changed?.Invoke());
        hub.On<Guid, bool>("MembershipChanged", (_, _) => MembershipChangedAsync());
        hub.Reconnected += _ => { Changed?.Invoke(); return Task.CompletedTask; };
        hub.Closed += _ => { Changed?.Invoke(); return Task.CompletedTask; };
        try { await hub.StartAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (diagnostics is not null) await diagnostics.ReportAsync("signalr.start", ex);
        }
        Changed?.Invoke();
    }
    public async Task WatchAsync(Guid listingId)
    {
        await StartAsync();
        if (Connected) await hub!.InvokeAsync("WatchListing", listingId);
    }
    public IDisposable? OnListingChanged(Action callback)
    {
        if (hub is null) return null;
        return new Subscriptions([
            hub.On<VoteDto, bool>("VoteChanged", (_, _) => callback()),
            hub.On<Guid, string>("CommentChanged", (_, _) => callback()),
            hub.On<Guid, bool>("ConsensusChanged", (_, _) => callback()),
            hub.On<Guid, bool>("MembershipChanged", (_, _) => callback())
        ]);
    }

    public async Task MembershipChangedAsync()
    {
        await auth.RefreshAsync();
        Changed?.Invoke();
    }

    private sealed class Subscriptions(IEnumerable<IDisposable> items) : IDisposable
    {
        public void Dispose() { foreach (var item in items) item.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (hub is not null) await hub.DisposeAsync();
    }
}
