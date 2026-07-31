using HouseConsensus.Shared;
using Microsoft.EntityFrameworkCore;

namespace HouseConsensus.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<ListingOverride> ListingOverrides => Set<ListingOverride>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<VoteNoteRevision> VoteNoteRevisions => Set<VoteNoteRevision>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<CommentRevision> CommentRevisions => Set<CommentRevision>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<AiRuleProposal> AiRuleProposals => Set<AiRuleProposal>();
    public DbSet<AiRuleProposalAction> AiRuleProposalActions => Set<AiRuleProposalAction>();
    public DbSet<AiRuleApplication> AiRuleApplications => Set<AiRuleApplication>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<MagicLink> MagicLinks => Set<MagicLink>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresEnum<MemberRole>(); b.HasPostgresEnum<VoteChoice>(); b.HasPostgresEnum<ListingState>(); b.HasPostgresEnum<ReasonTag>(); b.HasPostgresEnum<OverrideAction>();
        b.Entity<Member>(e => { e.ToTable("members"); e.HasKey(x => x.Id); e.HasIndex(x => x.Email).IsUnique(); e.Property(x => x.Email).HasMaxLength(320); e.Property(x => x.DisplayName).HasMaxLength(40); e.Property(x => x.AvatarColor).HasMaxLength(7); e.Property(x => x.Language).HasMaxLength(2); });
        b.Entity<Listing>(e => { e.ToTable("listings"); e.HasKey(x => x.Id); e.HasIndex(x => x.ExternalId).IsUnique(); e.HasIndex(x => new { x.State, x.FamilyFitScore }); e.Property(x => x.Price).HasPrecision(14, 2); e.Property(x => x.BuildableStatus).HasMaxLength(64); e.Property(x => x.Condition).HasMaxLength(64); e.Property(x => x.GardenOrientation).HasMaxLength(64); e.Property(x => x.MultigenFit).HasMaxLength(64); e.Property(x => x.PostalCode).HasMaxLength(16); e.Property(x => x.FamilyUnits).HasMaxLength(64); e.HasMany(x => x.Overrides).WithOne().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<ListingOverride>(e => { e.ToTable("listing_overrides"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.HasIndex(x => new { x.ListingId, x.CreatedAt }); e.HasOne<Member>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<Vote>(e => { e.ToTable("votes"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.Property(x => x.Tags).HasColumnType("reason_tag[]"); e.Property(x => x.Note).HasMaxLength(2000); e.HasMany(x => x.NoteRevisions).WithOne().HasForeignKey(x => x.VoteId).OnDelete(DeleteBehavior.Restrict); e.HasIndex(x => new { x.ListingId, x.MemberId, x.CreatedAt }); e.HasOne<Listing>().WithMany().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<VoteNoteRevision>(e => { e.ToTable("vote_note_revisions"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.Property(x => x.PreviousNote).HasMaxLength(2000); e.HasOne<Member>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<Comment>(e => { e.ToTable("comments"); e.HasKey(x => x.Id); e.Property(x => x.Body).HasMaxLength(4000); e.HasMany(x => x.Revisions).WithOne().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Restrict); e.HasIndex(x => new { x.ListingId, x.CreatedAt }); e.HasOne<Listing>().WithMany().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Member>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<CommentRevision>(e => { e.ToTable("comment_revisions"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.Property(x => x.PreviousBody).HasMaxLength(4000); e.HasOne<Member>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<AiRuleProposal>(e => { e.ToTable("ai_rule_proposals"); e.HasKey(x => x.Id); e.HasIndex(x => x.Version).IsUnique(); e.HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\""); e.Property(x => x.Summary).HasMaxLength(1000); e.Property(x => x.Status).HasMaxLength(20); e.Ignore(x => x.VersionLabel); e.HasOne<Member>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict); e.HasOne<Member>().WithMany().HasForeignKey(x => x.ReviewedById).OnDelete(DeleteBehavior.Restrict); e.HasOne<AiRuleProposal>().WithMany().HasForeignKey(x => x.PreviousProposalId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<AiRuleProposalAction>(e => { e.ToTable("ai_rule_proposal_actions"); e.HasKey(x => x.Id); e.Property(x => x.Action).HasMaxLength(40); e.HasIndex(x => new { x.ProposalId, x.CreatedAt }); e.HasOne<AiRuleProposal>().WithMany().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Member>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<AiRuleApplication>(e => { e.ToTable("ai_rule_applications"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.HasIndex(x => new { x.ProposalId, x.ListingId }).IsUnique(); e.HasOne<AiRuleProposal>().WithMany().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<Feedback>(e => { e.ToTable("feedback"); e.HasKey(x => x.Id); e.Property(x => x.Body).HasMaxLength(4000); e.HasIndex(x => x.CreatedAt); e.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Listing>().WithMany().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<Invite>(e => { e.ToTable("invites"); e.HasKey(x => x.Id); e.HasIndex(x => x.Email); e.Property(x => x.Email).HasMaxLength(320); e.HasOne<Member>().WithMany().HasForeignKey(x => x.InvitedById).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<MagicLink>(e => { e.ToTable("magic_links"); e.HasKey(x => x.Id); e.HasIndex(x => x.TokenHash).IsUnique(); e.Property(x => x.TokenHash).HasMaxLength(64); e.Property(x => x.Email).HasMaxLength(320); });
    }
}
