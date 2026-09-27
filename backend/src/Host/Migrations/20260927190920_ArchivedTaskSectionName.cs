using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalDashboard.V2.Host.Migrations
{
    /// <inheritdoc />
    public partial class ArchivedTaskSectionName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArchivedSectionName",
                schema: "tasks",
                table: "task_items",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE tasks.task_items AS task
                SET "ArchivedSectionName" = section."Name"
                FROM tasks.task_sections AS section
                WHERE task."SectionId" = section."Id" AND task."Location" = 'Archived';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedSectionName",
                schema: "tasks",
                table: "task_items");
        }
    }
}
