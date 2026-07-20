using HouseConsensus.Shared;
using Microsoft.EntityFrameworkCore;

namespace HouseConsensus.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<ListingOverride> ListingOverrides => Set<ListingOverride>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<CommentRevision> CommentRevisions => Set<CommentRevision>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<MagicLink> MagicLinks => Set<MagicLink>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresEnum<MemberRole>(); b.HasPostgresEnum<VoteChoice>(); b.HasPostgresEnum<ListingState>(); b.HasPostgresEnum<ReasonTag>(); b.HasPostgresEnum<OverrideAction>();
        b.Entity<Member>(e => { e.ToTable("members"); e.HasKey(x => x.Id); e.HasIndex(x => x.Email).IsUnique(); e.Property(x => x.Email).HasMaxLength(320); e.Property(x => x.Language).HasMaxLength(2); });
        b.Entity<Listing>(e => { e.ToTable("listings"); e.HasKey(x => x.Id); e.HasIndex(x => x.ExternalId).IsUnique(); e.HasIndex(x => new { x.State, x.FamilyFitScore }); e.Property(x => x.Price).HasPrecision(14, 2); e.HasMany(x => x.Overrides).WithOne().HasForeignKey(x => x.ListingId); });
        b.Entity<ListingOverride>(e => { e.ToTable("listing_overrides"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.HasIndex(x => new { x.ListingId, x.CreatedAt }); e.HasOne<Member>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<Vote>(e => { e.ToTable("votes"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.Property(x => x.Tags).HasColumnType("reason_tag[]"); e.HasIndex(x => new { x.ListingId, x.MemberId, x.CreatedAt }); e.HasOne<Listing>().WithMany().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<Comment>(e => { e.ToTable("comments"); e.HasKey(x => x.Id); e.Property(x => x.Body).HasMaxLength(4000); e.HasMany(x => x.Revisions).WithOne().HasForeignKey(x => x.CommentId); e.HasIndex(x => new { x.ListingId, x.CreatedAt }); e.HasOne<Listing>().WithMany().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Member>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<CommentRevision>(e => { e.ToTable("comment_revisions"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.Property(x => x.PreviousBody).HasMaxLength(4000); e.HasOne<Member>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<Feedback>(e => { e.ToTable("feedback"); e.HasKey(x => x.Id); e.Property(x => x.Body).HasMaxLength(4000); e.HasIndex(x => x.CreatedAt); e.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Listing>().WithMany().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<Invite>(e => { e.ToTable("invites"); e.HasKey(x => x.Id); e.HasIndex(x => x.Email); e.Property(x => x.Email).HasMaxLength(320); e.HasOne<Member>().WithMany().HasForeignKey(x => x.InvitedById).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<MagicLink>(e => { e.ToTable("magic_links"); e.HasKey(x => x.Id); e.HasIndex(x => x.TokenHash).IsUnique(); e.Property(x => x.TokenHash).HasMaxLength(64); e.Property(x => x.Email).HasMaxLength(320); });
    }
}
