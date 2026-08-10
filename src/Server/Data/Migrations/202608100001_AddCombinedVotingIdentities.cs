using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

public partial class AddCombinedVotingIdentities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "VotingIdentityId", table: "members", type: "uuid", nullable: false, defaultValue: Guid.Empty);
        migrationBuilder.Sql("UPDATE members SET \"VotingIdentityId\" = \"Id\" WHERE \"VotingIdentityId\" = '00000000-0000-0000-0000-000000000000'");
        migrationBuilder.CreateIndex(name: "IX_members_VotingIdentityId", table: "members", column: "VotingIdentityId");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_members_VotingIdentityId", table: "members");
        migrationBuilder.DropColumn(name: "VotingIdentityId", table: "members");
    }
}