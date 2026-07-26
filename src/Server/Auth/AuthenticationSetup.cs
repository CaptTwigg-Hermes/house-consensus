using System.Security.Claims;
using HouseConsensus.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HouseConsensus.Server.Auth;

public static class AuthenticationSetup
{
    public static CloudflareAccessOptions Add(IServiceCollection services, IConfiguration configuration, bool production)
    {
        var cloudflare = CloudflareAccessOptions.FromConfiguration(configuration);
        if (production && !cloudflare.Enabled)
            throw new InvalidOperationException("Cloudflare Access authentication is required in Production.");
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(cloudflare);
        if (cloudflare.Enabled)
        {
            services.AddHttpClient("CloudflareAccessJwks", client => client.Timeout = TimeSpan.FromSeconds(10));
            services.AddSingleton<ICloudflareJwtValidator>(provider => new CloudflareJwtValidator(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("CloudflareAccessJwks"),
                cloudflare,
                provider.GetRequiredService<TimeProvider>()));
            services.AddScoped<ICloudflareMemberService, CloudflareMemberService>();
            services.AddAuthentication(CloudflareAccessOptions.Scheme)
                .AddScheme<AuthenticationSchemeOptions, CloudflareAccessHandler>(CloudflareAccessOptions.Scheme, _ => { });
            return cloudflare;
        }

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "hc_session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = production ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.ExpireTimeSpan = TimeSpan.FromDays(30);
            o.SlidingExpiration = false;
            o.Events.OnValidatePrincipal = async context =>
            {
                var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!Guid.TryParse(id, out var memberId)) { context.RejectPrincipal(); return; }
                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var member = await db.Members.AsNoTracking().SingleOrDefaultAsync(x => x.Id == memberId && x.IsActive, context.HttpContext.RequestAborted);
                if (member is null)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync();
                }
            };
            o.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
        return cloudflare;
    }
}
