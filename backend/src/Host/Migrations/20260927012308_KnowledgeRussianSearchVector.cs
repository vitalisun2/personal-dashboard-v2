using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace PersonalDashboard.V2.Host.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeRussianSearchVector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                schema: "knowledge",
                table: "knowledge_nodes",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('russian', \"Title\" || ' ' || \"Markdown\")",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_nodes_SearchVector",
                schema: "knowledge",
                table: "knowledge_nodes",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_knowledge_nodes_SearchVector",
                schema: "knowledge",
                table: "knowledge_nodes");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                schema: "knowledge",
                table: "knowledge_nodes");
        }
    }
}
