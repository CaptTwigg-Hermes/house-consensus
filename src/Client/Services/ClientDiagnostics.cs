using System.Net.Http.Json;
using HouseConsensus.Shared;

namespace HouseConsensus.Client.Services;

public sealed class ClientDiagnostics(HttpClient http)
{
    public async Task ReportAsync(string area, Exception exception, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var fingerprintSource = string.Join('\n', exception.GetType().FullName, exception.Message, exception.StackTrace);
        var report = new ClientErrorReport(
            ClientDiagnosticContract.Area(area),
            ClientDiagnosticContract.ExceptionType(exception),
            DiagnosticText.Fingerprint(fingerprintSource));
        try
        {
            using var response = await http.PostAsJsonAsync("api/diagnostics/client-errors", report, ct);
        }
        catch when (!ct.IsCancellationRequested)
        {
            // Diagnostics must never replace the original application failure.
        }
    }
}
