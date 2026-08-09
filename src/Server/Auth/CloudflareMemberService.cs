using HouseConsensus.Server.Data;
using HouseConsensus.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HouseConsensus.Server.Auth;

public interface ICloudflareMemberService
{
    Task<Member?> ResolveAsync(string email, CancellationToken ct);
}

public sealed class CloudflareMemberService(
    AppDbContext db,
    ILogger<CloudflareMemberService>? logger = null) : ICloudflareMemberService
{
    private readonly ILogger<CloudflareMemberService> _logger = logger ?? NullLogger<CloudflareMemberService>.Instance;
    public async Task<Member?> ResolveAsync(string email, CancellationToken ct)
    {
        email = MagicLinkService.Normalize(email);
        var existing = await db.Members.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email, ct);
        if (existing?.IsActive == true)
        {
            _logger.LogDebug(
                new EventId(DiagnosticEventIds.CloudflareMemberResolved, nameof(DiagnosticEventIds.CloudflareMemberResolved)),
                "Resolved active Cloudflare member {MemberId} with role {MemberRole}",
                existing.Id,
                existing.Role);
            return existing;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({email}, 0))", ct);

        var member = await db.Members.SingleOrDefaultAsync(x => x.Email == email, ct);
        if (member is not null)
        {
            member.Reactivate();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            _logger.LogInformation(
                new EventId(DiagnosticEventIds.CloudflareMemberResolved, nameof(DiagnosticEventIds.CloudflareMemberResolved)),
                "Reactivated Cloudflare member {MemberId} with role {MemberRole}",
                member.Id,
                member.Role);
            return member;
        }

        member = new Member { Email = email, Role = MemberRole.Member };
        db.Members.Add(member);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        _logger.LogInformation(
            new EventId(DiagnosticEventIds.CloudflareMemberResolved, nameof(DiagnosticEventIds.CloudflareMemberResolved)),
            "Provisioned Cloudflare member {MemberId} with role {MemberRole}",
            member.Id,
            member.Role);
        return member;
    }
}
