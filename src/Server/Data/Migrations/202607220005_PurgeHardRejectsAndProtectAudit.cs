using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;
[DbContext(typeof(AppDbContext)), Migration("202607220005_PurgeHardRejectsAndProtectAudit")]
public sealed class PurgeHardRejectsAndProtectAudit : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
CREATE TEMP TABLE purge_hard_reject_ids (id uuid PRIMARY KEY) ON COMMIT DROP;
INSERT INTO purge_hard_reject_ids SELECT "Id" FROM listings WHERE "State"::text='filter_rejected';
DO $purge$
BEGIN
  IF to_regclass('public.listing_export_state') IS NOT NULL THEN
    EXECUTE 'INSERT INTO purge_hard_reject_ids SELECT listing_id FROM listing_export_state WHERE pipeline_decision=''filter_rejected'' ON CONFLICT DO NOTHING';
  END IF;
  IF EXISTS (
    SELECT 1 FROM purge_hard_reject_ids p
    WHERE EXISTS (SELECT 1 FROM votes v WHERE v."ListingId"=p.id)
       OR EXISTS (SELECT 1 FROM comments c WHERE c."ListingId"=p.id)
       OR EXISTS (SELECT 1 FROM feedback f WHERE f."ListingId"=p.id)
       OR EXISTS (SELECT 1 FROM listing_overrides o WHERE o."ListingId"=p.id)
  ) THEN RAISE EXCEPTION 'Cannot purge hard-filter rejects: user history exists.'; END IF;
  IF to_regclass('public.listing_media') IS NOT NULL THEN EXECUTE 'DELETE FROM listing_media WHERE listing_id IN (SELECT id FROM purge_hard_reject_ids)'; END IF;
  IF to_regclass('public.ai_evidence') IS NOT NULL THEN EXECUTE 'DELETE FROM ai_evidence WHERE listing_id IN (SELECT id FROM purge_hard_reject_ids)'; END IF;
  IF to_regclass('public.listing_imports') IS NOT NULL THEN EXECUTE 'DELETE FROM listing_imports WHERE listing_id IN (SELECT id FROM purge_hard_reject_ids)'; END IF;
  IF to_regclass('public.listing_export_state') IS NOT NULL THEN EXECUTE 'DELETE FROM listing_export_state WHERE listing_id IN (SELECT id FROM purge_hard_reject_ids)'; END IF;
END $purge$;
DELETE FROM listings WHERE "Id" IN (SELECT id FROM purge_hard_reject_ids);

ALTER TABLE listing_overrides DROP CONSTRAINT IF EXISTS "listing_overrides_ListingId_fkey";
ALTER TABLE listing_overrides ADD CONSTRAINT "FK_listing_overrides_listings_ListingId" FOREIGN KEY ("ListingId") REFERENCES listings("Id") ON DELETE RESTRICT;
ALTER TABLE comment_revisions DROP CONSTRAINT IF EXISTS "comment_revisions_CommentId_fkey";
ALTER TABLE comment_revisions ADD CONSTRAINT "FK_comment_revisions_comments_CommentId" FOREIGN KEY ("CommentId") REFERENCES comments("Id") ON DELETE RESTRICT;
ALTER TABLE vote_note_revisions DROP CONSTRAINT IF EXISTS "vote_note_revisions_VoteId_fkey";
ALTER TABLE vote_note_revisions ADD CONSTRAINT "FK_vote_note_revisions_votes_VoteId" FOREIGN KEY ("VoteId") REFERENCES votes("Id") ON DELETE RESTRICT;
""");
    protected override void Down(MigrationBuilder m) => m.Sql("""
ALTER TABLE listing_overrides DROP CONSTRAINT IF EXISTS "FK_listing_overrides_listings_ListingId";
ALTER TABLE listing_overrides ADD CONSTRAINT "listing_overrides_ListingId_fkey" FOREIGN KEY ("ListingId") REFERENCES listings("Id") ON DELETE CASCADE;
ALTER TABLE comment_revisions DROP CONSTRAINT IF EXISTS "FK_comment_revisions_comments_CommentId";
ALTER TABLE comment_revisions ADD CONSTRAINT "comment_revisions_CommentId_fkey" FOREIGN KEY ("CommentId") REFERENCES comments("Id") ON DELETE CASCADE;
ALTER TABLE vote_note_revisions DROP CONSTRAINT IF EXISTS "FK_vote_note_revisions_votes_VoteId";
ALTER TABLE vote_note_revisions ADD CONSTRAINT "vote_note_revisions_VoteId_fkey" FOREIGN KEY ("VoteId") REFERENCES votes("Id") ON DELETE CASCADE;
""");
}
