using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalDashboard.V2.Host.Migrations
{
    /// <inheritdoc />
    public partial class SharedPersistentTaskSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBacklogVisible",
                schema: "tasks",
                table: "task_sections",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql("UPDATE tasks.task_sections SET \"IsBacklogVisible\" = (\"Location\" = 'Backlog');");
            migrationBuilder.DropIndex(name: "IX_task_sections_Location_Name", schema: "tasks", table: "task_sections");
            migrationBuilder.Sql("""
                CREATE TEMP TABLE shared_task_section_merges ON COMMIT DROP AS
                SELECT today."Id" AS "RemovedId", today."Version" AS "RemovedVersion", backlog."Id" AS "CanonicalId"
                FROM tasks.task_sections AS today
                JOIN tasks.task_sections AS backlog ON backlog."Name" = today."Name" AND backlog."Location" = 'Backlog'
                WHERE today."Location" = 'Today';

                UPDATE tasks.task_items AS item
                SET "SectionId" = merged."CanonicalId"
                FROM shared_task_section_merges AS merged
                WHERE item."SectionId" = merged."RemovedId";

                DELETE FROM tasks.task_sections AS today
                USING shared_task_section_merges AS merged
                WHERE today."Id" = merged."RemovedId";

                UPDATE tasks.task_sections SET "Location" = 'Backlog' WHERE "Location" = 'Today';

                INSERT INTO tasks.task_sections ("Id", "Name", "Location", "Position", "Version", "IsBacklogVisible")
                SELECT gen_random_uuid(), legacy."ArchivedSectionName", 'Backlog',
                       (COALESCE((SELECT max("Position") FROM tasks.task_sections), -1) + row_number() OVER (ORDER BY legacy."ArchivedSectionName"))::integer,
                       1, false
                FROM (
                    SELECT DISTINCT item."ArchivedSectionName"
                    FROM tasks.task_items AS item
                    WHERE item."ProjectId" IS NULL
                      AND item."Location" = 'Archived'
                      AND item."SectionId" IS NULL
                      AND item."ArchivedSectionName" IS NOT NULL
                      AND btrim(item."ArchivedSectionName") <> ''
                ) AS legacy
                WHERE NOT EXISTS (
                    SELECT 1 FROM tasks.task_sections AS section WHERE section."Name" = legacy."ArchivedSectionName"
                );

                UPDATE tasks.task_items AS item
                SET "SectionId" = section."Id"
                FROM tasks.task_sections AS section
                WHERE item."ProjectId" IS NULL
                  AND item."Location" = 'Archived'
                  AND item."SectionId" IS NULL
                  AND item."ArchivedSectionName" = section."Name";
                """);
            migrationBuilder.CreateIndex(
                name: "IX_task_sections_Location_Name",
                schema: "tasks",
                table: "task_sections",
                columns: new[] { "Location", "Name" },
                unique: true);
            migrationBuilder.Sql("""
                WITH snapshots AS MATERIALIZED (
                    SELECT 'tasks.task'::character varying(160) AS "Type", item."Id", item."Version", false AS "Deleted",
                           COALESCE(latest."PayloadJson", '{}'::jsonb) || jsonb_build_object(
                               'placement', lower(item."Location"),
                               'workStatus', lower(item."WorkStatus"),
                               'sectionId', item."SectionId",
                               'archivedSectionName', item."ArchivedSectionName",
                               'position', item."Position",
                               'planningPosition', item."PlanningPosition") AS "PayloadJson"
                    FROM tasks.task_items AS item
                    LEFT JOIN LATERAL (
                        SELECT journal."PayloadJson" FROM platform.entity_change_journal AS journal
                        WHERE journal."Type" = 'tasks.task' AND journal."Id" = item."Id" AND NOT journal."Deleted"
                        ORDER BY journal."Sequence" DESC LIMIT 1
                    ) AS latest ON true
                    WHERE item."SectionId" IS NOT NULL

                    UNION ALL

                    SELECT 'tasks.section'::character varying(160), section."Id", section."Version", false,
                           COALESCE(latest."PayloadJson", '{}'::jsonb) || jsonb_build_object(
                               'title', section."Name",
                               'bucket', 'backlog',
                               'location', 'backlog',
                               'position', section."Position",
                               'isBacklogVisible', section."IsBacklogVisible",
                               'url', '/tasks')
                    FROM tasks.task_sections AS section
                    LEFT JOIN LATERAL (
                        SELECT journal."PayloadJson" FROM platform.entity_change_journal AS journal
                        WHERE journal."Type" = 'tasks.section' AND journal."Id" = section."Id" AND NOT journal."Deleted"
                        ORDER BY journal."Sequence" DESC LIMIT 1
                    ) AS latest ON true
                    UNION ALL

                    SELECT 'tasks.section'::character varying(160), merged."RemovedId", merged."RemovedVersion" + 1, true, NULL::jsonb
                    FROM shared_task_section_merges AS merged
                ), numbered AS MATERIALIZED (
                    SELECT snapshots.*, row_number() OVER (ORDER BY "Type", "Id") AS row_number,
                           count(*) OVER () AS total
                    FROM snapshots
                ), advanced_cursor AS (
                    UPDATE platform.entity_change_cursor
                    SET "Sequence" = "Sequence" + (SELECT count(*) FROM numbered)
                    WHERE "Id" = 1
                    RETURNING "Sequence"
                )
                INSERT INTO platform.entity_change_journal ("Sequence", "Type", "Id", "Version", "Deleted", "ImportedFromPeer", "PayloadJson")
                SELECT advanced_cursor."Sequence" - numbered.total + numbered.row_number,
                       numbered."Type", numbered."Id", numbered."Version", numbered."Deleted", false, numbered."PayloadJson"
                FROM numbered CROSS JOIN advanced_cursor;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_task_sections_Location_Name", schema: "tasks", table: "task_sections");
            migrationBuilder.Sql("""
                UPDATE tasks.task_items SET "SectionId" = NULL WHERE "Location" = 'Archived';

                DO $$
                DECLARE
                    section_row record;
                    today_id uuid;
                BEGIN
                    FOR section_row IN
                        SELECT section."Id", section."Name", section."Position", section."Version", section."IsBacklogVisible"
                        FROM tasks.task_sections AS section
                        WHERE EXISTS (SELECT 1 FROM tasks.task_items AS item WHERE item."SectionId" = section."Id" AND item."Location" = 'Today')
                    LOOP
                        IF NOT section_row."IsBacklogVisible"
                           AND NOT EXISTS (SELECT 1 FROM tasks.task_items AS item WHERE item."SectionId" = section_row."Id" AND item."Location" = 'Backlog') THEN
                            UPDATE tasks.task_sections SET "Location" = 'Today' WHERE "Id" = section_row."Id";
                        ELSE
                            today_id := gen_random_uuid();
                            INSERT INTO tasks.task_sections ("Id", "Name", "Location", "Position", "Version")
                            VALUES (today_id, section_row."Name", 'Today', section_row."Position", section_row."Version");
                            UPDATE tasks.task_items SET "SectionId" = today_id
                            WHERE "SectionId" = section_row."Id" AND "Location" = 'Today';
                        END IF;
                    END LOOP;
                END $$;
                """);
            migrationBuilder.CreateIndex(
                name: "IX_task_sections_Location_Name",
                schema: "tasks",
                table: "task_sections",
                columns: new[] { "Location", "Name" },
                unique: true);
            migrationBuilder.DropColumn(
                name: "IsBacklogVisible",
                schema: "tasks",
                table: "task_sections");
        }
    }
}
