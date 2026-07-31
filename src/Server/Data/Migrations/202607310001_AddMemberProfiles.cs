using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202607310001_AddMemberProfiles")]
public sealed class AddMemberProfiles : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE members ADD COLUMN IF NOT EXISTS "AvatarColor" character varying(7) NOT NULL DEFAULT '';
ALTER TABLE members DROP CONSTRAINT IF EXISTS "CK_members_AvatarColor";
ALTER TABLE members ADD CONSTRAINT "CK_members_AvatarColor" CHECK ("AvatarColor" IN ('', '#7c2d12', '#9f1239', '#86198f', '#6d28d9', '#3730a3', '#1e40af', '#075985', '#0f766e', '#166534', '#3f6212', '#854d0e', '#9a3412'));
""");

    protected override void Down(MigrationBuilder m) => m.Sql("""
ALTER TABLE members DROP CONSTRAINT IF EXISTS "CK_members_AvatarColor";
ALTER TABLE members DROP COLUMN IF EXISTS "AvatarColor";
""");
}
