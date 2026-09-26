using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalDashboard.V2.Host.Migrations
{
    /// <inheritdoc />
    public partial class V1PeerSyncCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ImportedFromPeer",
                schema: "platform",
                table: "entity_change_journal",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "v1_peer_cursor",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_v1_peer_cursor", x => x.Id);
                });

            // Start from the live journal head: only future changes are synchronized.
            migrationBuilder.Sql("INSERT INTO platform.v1_peer_cursor (\"Id\", \"Sequence\") SELECT 1, \"Sequence\" FROM platform.entity_change_cursor WHERE \"Id\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "v1_peer_cursor",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "ImportedFromPeer",
                schema: "platform",
                table: "entity_change_journal");
        }
    }
}
