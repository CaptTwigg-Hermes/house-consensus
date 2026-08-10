using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HouseConsensus.Shared;

public sealed record ClientErrorReport(string Area, string ExceptionType, string Fingerprint);

public static class ClientDiagnosticContract
{
    private static readonly HashSet<string> Areas = ["startup", "render", "signalr.start", "auth.initialize"];
    private static readonly HashSet<string> ExceptionTypes =
    [
        "ArgumentException",
        "Exception",
        "HttpRequestException",
        "HubException",
        "InvalidOperationException",
        "JSException",
        "JsonException",
        "NullReferenceException",
        "OperationCanceledException",
        "TaskCanceledException",
    ];

    public static string Area(string? value) => value is not null && Areas.Contains(value) ? value : "unknown";
    public static bool IsArea(string? value) => value is not null && Areas.Contains(value);
    public static string ExceptionType(Exception exception) =>
        ExceptionTypes.Contains(exception.GetType().Name) ? exception.GetType().Name : "Other";
    public static bool IsExceptionType(string? value) =>
        value == "Other" || value is not null && ExceptionTypes.Contains(value);
}

public static class DiagnosticEventIds
{
    public const int BootstrapLifecycle = 101;
    public const int CloudflareAuthenticationRejected = 1101;
    public const int CloudflareValidationFailed = 1102;
    public const int CloudflareKeysRefreshed = 1103;
    public const int CloudflareMemberResolved = 1104;
    public const int MagicLinkLifecycle = 1201;
    public const int SignalRLifecycle = 1301;
    public const int ListingImageRejected = 2101;
    public const int ListingLookupFailed = 2201;
    public const int EmailDelivery = 2301;
    public const int AiGenerationFailed = 3101;
    public const int AiGenerationCompleted = 3102;
    public const int AiProposalLifecycle = 3201;
    public const int ClientApplicationError = 4101;
    public const int ManualScoringLifecycle = 5101;
}

public static class DiagnosticText
{
    private const int MaximumLength = 4096;
    private static readonly Regex AbsoluteUrl = new(
        @"https?://\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex SecretParameter = new(
        @"(?<name>(?:access_token|token|code|key|password)=)[^&\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var bounded = value.Length <= MaximumLength ? value : value[..MaximumLength];
        bounded = AbsoluteUrl.Replace(bounded, match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Path)
                : "[REDACTED-URL]");
        bounded = SecretParameter.Replace(bounded, "${name}[REDACTED]");
        return string.Concat(bounded.Select(character => char.IsControl(character) ? ' ' : character));
    }


    public static string Fingerprint(string? value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    public static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
