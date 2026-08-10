using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace HouseConsensus.Client.Services;

public sealed class ClientErrorBoundary : ErrorBoundary
{
    [Inject] public ClientDiagnostics Diagnostics { get; set; } = null!;

    protected override Task OnErrorAsync(Exception exception) =>
        Diagnostics.ReportAsync("render", exception);
}
