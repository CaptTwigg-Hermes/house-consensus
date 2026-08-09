using HouseConsensus.Shared;

namespace HouseConsensus.Server.Diagnostics;

public sealed class ClientDiagnosticSink(ILogger<ClientDiagnosticSink> logger)
{
    public void Record(ClientErrorReport report, Guid memberId, string traceId)
    {
        logger.LogError(
            new EventId(DiagnosticEventIds.ClientApplicationError, nameof(DiagnosticEventIds.ClientApplicationError)),
            "Client application error in {Area} for member {MemberId}, trace {TraceId}: {ExceptionType}, fingerprint {ErrorFingerprint}",
            ClientDiagnosticContract.Area(report.Area),
            memberId,
            DiagnosticText.Sanitize(traceId),
            ClientDiagnosticContract.IsExceptionType(report.ExceptionType) ? report.ExceptionType : "unknown",
            DiagnosticText.IsFingerprint(report.Fingerprint) ? report.Fingerprint : "invalid");
    }
}
