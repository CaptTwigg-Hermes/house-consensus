using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace HouseConsensus.Server.Auth;

public sealed class CloudflareAccessHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ICloudflareJwtValidator validator,
    ICloudflareMemberService members)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var assertion = Request.Headers["Cf-Access-Jwt-Assertion"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(assertion)) return AuthenticateResult.Fail("Cloudflare Access assertion is required.");

        var email = await validator.ValidateAsync(assertion, Context.RequestAborted);
        if (email is null) return AuthenticateResult.Fail("Cloudflare Access assertion is invalid.");
        var member = await members.ResolveAsync(email, Context.RequestAborted);
        if (member is null) return AuthenticateResult.Fail("Cloudflare Access user could not be provisioned.");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, member.Id.ToString()),
            new Claim(ClaimTypes.Email, member.Email),
            new Claim(ClaimTypes.Role, member.Role.ToString()),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
