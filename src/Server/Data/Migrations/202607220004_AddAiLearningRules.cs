using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;
[DbContext(typeof(AppDbContext)), Migration("202607220004_AddAiLearningRules")]
public sealed class AddAiLearningRules : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings ADD COLUMN "LearningRuleVersion" text NULL;
CREATE TABLE ai_rule_proposals (
    "Id" uuid PRIMARY KEY, "CreatedById" uuid NOT NULL REFERENCES members("Id") ON DELETE RESTRICT,
    "Version" integer NOT NULL, "Summary" varchar(1000) NOT NULL, "RuleJson" text NOT NULL,
    "SupportingNotesJson" text NOT NULL, "ImpactPreviewJson" text NOT NULL,
    "Status" varchar(20) NOT NULL, "IsActive" boolean NOT NULL, "CreatedAt" timestamptz NOT NULL,
    "ReviewedById" uuid NULL REFERENCES members("Id") ON DELETE RESTRICT, "ReviewedAt" timestamptz NULL
);
CREATE UNIQUE INDEX "IX_ai_rule_proposals_Version" ON ai_rule_proposals("Version");
CREATE INDEX "IX_ai_rule_proposals_IsActive" ON ai_rule_proposals("IsActive");
""");
    protected override void Down(MigrationBuilder m) => m.Sql("""DROP TABLE ai_rule_proposals; ALTER TABLE listings DROP COLUMN "LearningRuleVersion";""");
}
