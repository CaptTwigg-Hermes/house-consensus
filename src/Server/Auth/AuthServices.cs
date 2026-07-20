using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using HouseConsensus.Server.Data;
using HouseConsensus.Shared;
using Microsoft.EntityFrameworkCore;

namespace HouseConsensus.Server.Auth;

public interface IEmailSender { Task SendMagicLinkAsync(string email, string link, CancellationToken ct); }
public sealed class SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendMagicLinkAsync(string email, string link, CancellationToken ct)
    {
        var host = config["Email:SmtpHost"] ?? "mailpit"; var port = config.GetValue("Email:SmtpPort", 1025);
        using var client = new SmtpClient(host, port); using var message = new MailMessage(config["Email:From"] ?? "no-reply@house-consensus.local", email, "Your House Consensus sign-in link", "This link expires in 15 minutes and can be used once:\n\n" + link);
        await client.SendMailAsync(message, ct); logger.LogInformation("Sent a magic link to {Email}", email);
    }
}
public sealed class MagicLinkService(AppDbContext db, IEmailSender mail, IConfiguration config, TimeProvider clock)
{
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
    public async Task RequestAsync(string email, CancellationToken ct)
    {
        email = Normalize(email); var now = clock.GetUtcNow();
        var known = await db.Members.AnyAsync(x => x.Email == email && x.IsActive, ct) || await db.Invites.AnyAsync(x => x.Email == email && x.AcceptedAt == null && x.ExpiresAt > now, ct);
        if (!known) return; // prevent account enumeration
        var bytes = RandomNumberGenerator.GetBytes(32); var token = Base64Url(bytes);
        db.MagicLinks.Add(new MagicLink { Email = email, TokenHash = Hash(token), ExpiresAt = now.AddMinutes(15) }); await db.SaveChangesAsync(ct);
        var origin = (config["PublicOrigin"] ?? "http://localhost:8080").TrimEnd('/'); await mail.SendMagicLinkAsync(email, $"{origin}/api/auth/consume?token={Uri.EscapeDataString(token)}", ct);
    }
    public async Task<Member?> ConsumeAsync(string token, CancellationToken ct)
    {
        var hash = Hash(token); var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var item = await db.MagicLinks.FromSqlInterpolated($"SELECT * FROM magic_links WHERE \"TokenHash\" = {hash} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (item is null || !item.IsValid(now)) return null;
        item.ConsumedAt = now;
        var member = await db.Members.SingleOrDefaultAsync(x => x.Email == item.Email, ct);
        if (member is null)
        {
            var invite = await db.Invites.Where(x => x.Email == item.Email && x.AcceptedAt == null && x.ExpiresAt > now).OrderByDescending(x => x.ExpiresAt).FirstOrDefaultAsync(ct);
            if (invite is null) return null;
            invite.AcceptedAt = now; member = new Member { Email = item.Email, Role = MemberRole.Member }; db.Members.Add(member);
        }
        if (!member.IsActive) return null;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return member;
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
