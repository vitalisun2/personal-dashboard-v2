using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalDashboard.V2.Host.Migrations
{
    /// <inheritdoc />
    public partial class PlanningTaskDisplayOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PlanningPosition",
                schema: "tasks",
                table: "task_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE tasks.task_items SET "PlanningPosition" = "Position";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlanningPosition",
                schema: "tasks",
                table: "task_items");
        }
    }
}
