using Microsoft.Extensions.Configuration;

namespace HouseConsensus.Server.Auth;

public sealed record CloudflareAccessOptions(bool Enabled, string? TeamDomain, string? Audience)
{
    public const string Scheme = "CloudflareAccess";

    public string Issuer => $"https://{TeamDomain}";
    public Uri JwksUri => new($"{Issuer}/cdn-cgi/access/certs");

    public static CloudflareAccessOptions FromConfiguration(IConfiguration configuration)
    {
        var enabled = configuration.GetValue("CloudflareAccess:Enabled", false);
        if (!enabled) return new(false, null, null);

        var teamDomain = configuration["CloudflareAccess:TeamDomain"]?.Trim().ToLowerInvariant();
        var audience = configuration["CloudflareAccess:Audience"]?.Trim();
        if (string.IsNullOrWhiteSpace(teamDomain)
            || !Uri.TryCreate("https://" + teamDomain, UriKind.Absolute, out var teamUri)
            || teamUri.Host != teamDomain
            || teamUri.AbsolutePath != "/"
            || !teamDomain.EndsWith(".cloudflareaccess.com", StringComparison.Ordinal)
            || teamDomain.Length <= ".cloudflareaccess.com".Length)
            throw new InvalidOperationException("CloudflareAccess:TeamDomain must be the team hostname, for example team.cloudflareaccess.com.");
        if (string.IsNullOrWhiteSpace(audience)
            || audience.IndexOfAny(['*', '?']) >= 0
            || audience.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("CloudflareAccess:Audience must be the exact Access application audience with no wildcards or whitespace.");
        return new(true, teamDomain, audience);
    }
}
