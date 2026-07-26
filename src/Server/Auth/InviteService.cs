using HouseConsensus.Server.Data;
using HouseConsensus.Shared;
using Microsoft.EntityFrameworkCore;

namespace HouseConsensus.Server.Auth;

public sealed class InviteService(AppDbContext db, MagicLinkService links, CloudflareAccessOptions cloudflare, TimeProvider clock)
{
    public async Task<Invite> CreateAsync(string email, Guid invitedById, CancellationToken ct)
    {
        email = MagicLinkService.Normalize(email);
        if (await db.Members.AnyAsync(x => x.Email == email, ct)) throw new InviteConflictException();
        var invite = new Invite { Email = email, InvitedById = invitedById, ExpiresAt = clock.GetUtcNow().AddDays(7) };
        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct);
        if (!cloudflare.Enabled) await links.RequestAsync(email, ct);
        return invite;
    }
}

public sealed class InviteConflictException : Exception;
