using HouseConsensus.Server.Data;
using HouseConsensus.Shared;
using Microsoft.EntityFrameworkCore;

namespace HouseConsensus.Server.Auth;

public interface ICloudflareMemberService
{
    Task<Member?> ResolveAsync(string email, CancellationToken ct);
}

public sealed class CloudflareMemberService(AppDbContext db, TimeProvider clock) : ICloudflareMemberService
{
    public async Task<Member?> ResolveAsync(string email, CancellationToken ct)
    {
        email = MagicLinkService.Normalize(email);
        var existing = await db.Members.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email, ct);
        if (existing is not null) return existing.IsActive ? existing : null;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({email}, 0))", ct);

        var member = await db.Members.SingleOrDefaultAsync(x => x.Email == email, ct);
        if (member is not null)
        {
            if (!member.IsActive) return null;
            await transaction.CommitAsync(ct);
            return member;
        }

        var now = clock.GetUtcNow();
        var invite = await db.Invites
            .Where(x => x.Email == email && x.AcceptedAt == null && x.ExpiresAt > now)
            .OrderByDescending(x => x.ExpiresAt)
            .FirstOrDefaultAsync(ct);
        if (invite is null) return null;

        invite.AcceptedAt = now;
        member = new Member { Email = email, Role = MemberRole.Member };
        db.Members.Add(member);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return member;
    }
}
