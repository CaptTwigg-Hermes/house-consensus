using System.Security.Claims;
using HouseConsensus.Server.Data;
using HouseConsensus.Shared;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace HouseConsensus.Server.Auth;

public sealed class DebugAutoLoginMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public static void EnsureSafe(bool enabled, string environmentName)
    {
        if (enabled && !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Debug auto-login can only be enabled in Development.");
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            var email = MagicLinkService.Normalize(configuration["INITIAL_OWNER_EMAIL"] ?? "");
            var member = string.IsNullOrWhiteSpace(email)
                ? null
                : await db.Members.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email && x.IsActive && x.Role == MemberRole.Owner, context.RequestAborted);
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
