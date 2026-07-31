using System.Security.Claims;
using HouseConsensus.Server.Data;
using HouseConsensus.Shared;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace HouseConsensus.Server.Auth;

public sealed class DebugAutoLoginMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public const string E2EEmailHeader = "X-House-Consensus-E2E-Email";

    public static void EnsureSafe(bool enabled, string environmentName)
    {
        if (enabled && !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Debug auto-login can only be enabled in Development.");
    }

    public static void EnsureE2ETestAuthSafe(bool enabled, bool debugAutoLogin, bool seedData, string environmentName)
    {
        if (!enabled) return;
        if (!debugAutoLogin || !seedData || !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("E2E test authentication requires seed data and debug auto-login in Development.");
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            var testAuth = configuration.GetValue("E2E:TestAuth", false);
            var email = MagicLinkService.Normalize(testAuth
                ? context.Request.Headers[E2EEmailHeader].FirstOrDefault() ?? ""
                : configuration["INITIAL_OWNER_EMAIL"] ?? "");
            Member? member = null;
            if (!string.IsNullOrWhiteSpace(email))
            {
                ICloudflareMemberService? cloudflareMembers = null;
                if (testAuth && context.RequestServices is { } services)
                    cloudflareMembers = services.GetService<ICloudflareMemberService>();
                member = cloudflareMembers is not null
                    ? await cloudflareMembers.ResolveAsync(email, context.RequestAborted)
                    : await db.Members.AsNoTracking().SingleOrDefaultAsync(
                        x => x.Email == email && x.IsActive && (testAuth || x.Role == MemberRole.Owner),
                        context.RequestAborted);
            }
            if (member is not null)
            {
                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, member.Id.ToString()),
                    new Claim(ClaimTypes.Email, member.Email),
                    new Claim(ClaimTypes.Role, member.Role.ToString()),
                };
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            }
        }
        await next(context);
    }
}
