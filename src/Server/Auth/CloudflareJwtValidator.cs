using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HouseConsensus.Server.Auth;

public interface ICloudflareJwtValidator
{
    Task<string?> ValidateAsync(string? token, CancellationToken ct);
}

public sealed class CloudflareJwtValidator(HttpClient http, CloudflareAccessOptions options, TimeProvider clock) : ICloudflareJwtValidator
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyDictionary<string, RSAParameters> _keys = new Dictionary<string, RSAParameters>();
    private DateTimeOffset _keysExpireAt;
    private DateTimeOffset _lastRefreshAttemptAt;

    public async Task<string?> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16 * 1024) return null;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) return null;

        try
        {
            using var header = JsonDocument.Parse(Decode(parts[0]));
            if (header.RootElement.ValueKind != JsonValueKind.Object
                || !header.RootElement.TryGetProperty("alg", out var alg)
                || alg.GetString() != "RS256"
                || !header.RootElement.TryGetProperty("kid", out var kidElement)) return null;
            var kid = kidElement.GetString();
            if (string.IsNullOrWhiteSpace(kid)) return null;

            var key = await GetKeyAsync(kid, false, ct);
            if (key is null) key = await GetKeyAsync(kid, true, ct);
            if (key is null) return null;

            using var rsa = RSA.Create();
            rsa.ImportParameters(key.Value);
            var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!rsa.VerifyData(signingInput, Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return null;

            using var payload = JsonDocument.Parse(Decode(parts[1]));
            var claims = payload.RootElement;
            if (claims.ValueKind != JsonValueKind.Object
                || !StringClaimEquals(claims, "iss", options.Issuer)
                || !AudienceMatches(claims, options.Audience!)
                || !TryUnixTime(claims, "exp", out var expires)
                || !TryUnixTime(claims, "nbf", out var notBefore)
                || expires <= clock.GetUtcNow()
                || notBefore > clock.GetUtcNow()
                || !claims.TryGetProperty("email", out var emailElement)) return null;
            var email = emailElement.GetString()?.Trim().ToLowerInvariant();
            return !string.IsNullOrWhiteSpace(email) && MailAddress.TryCreate(email, out var parsed) && parsed.Address == email ? email : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or HttpRequestException or TaskCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private async Task<RSAParameters?> GetKeyAsync(string kid, bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && clock.GetUtcNow() < _keysExpireAt)
            return _keys.TryGetValue(kid, out var cached) ? cached : null;
        if (clock.GetUtcNow() < _lastRefreshAttemptAt.AddMinutes(5)) return null;
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && clock.GetUtcNow() < _keysExpireAt)
                return _keys.TryGetValue(kid, out var cachedAfterLock) ? cachedAfterLock : null;
            if (clock.GetUtcNow() < _lastRefreshAttemptAt.AddMinutes(5)) return null;
            _lastRefreshAttemptAt = clock.GetUtcNow();
            using var response = await http.GetAsync(options.JwksUri, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var fresh = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.GetProperty("keys").EnumerateArray())
            {
                if (!StringClaimEquals(item, "kty", "RSA") || !StringClaimEquals(item, "alg", "RS256")) continue;
                var keyId = item.GetProperty("kid").GetString();
                if (string.IsNullOrWhiteSpace(keyId)) continue;
                fresh[keyId] = new RSAParameters { Modulus = Decode(item.GetProperty("n").GetString()!), Exponent = Decode(item.GetProperty("e").GetString()!) };
            }
            if (fresh.Count == 0)
                throw new InvalidOperationException("Cloudflare JWKS contained no usable RSA signing keys.");
            _keys = fresh;
            _keysExpireAt = clock.GetUtcNow().AddHours(6);
            return fresh.TryGetValue(kid, out var found) ? found : null;
        }
        finally { _refreshLock.Release(); }
    }

    private static bool StringClaimEquals(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out var claim) && claim.ValueKind == JsonValueKind.String && string.Equals(claim.GetString(), expected, StringComparison.Ordinal);

    private static bool AudienceMatches(JsonElement claims, string expected)
    {
        if (!claims.TryGetProperty("aud", out var aud)) return false;
        if (aud.ValueKind == JsonValueKind.String) return string.Equals(aud.GetString(), expected, StringComparison.Ordinal);
        return aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && string.Equals(x.GetString(), expected, StringComparison.Ordinal));
    }

    private static bool TryUnixTime(JsonElement claims, string name, out DateTimeOffset value)
    {
        value = default;
        return claims.TryGetProperty(name, out var claim) && claim.TryGetInt64(out var seconds) && TryUnix(seconds, out value);
    }

    private static bool TryUnix(long seconds, out DateTimeOffset value)
    {
        try { value = DateTimeOffset.FromUnixTimeSeconds(seconds); return true; }
        catch (ArgumentOutOfRangeException) { value = default; return false; }
    }

    private static byte[] Decode(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        value += (value.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException("Invalid base64url encoding.") };
        return Convert.FromBase64String(value);
    }
}
