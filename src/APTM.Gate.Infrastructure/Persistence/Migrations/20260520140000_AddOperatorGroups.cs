using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APTM.Gate.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phase 0 of the Operator Groups feature on the gate (see DESIGN_OPERATOR_GROUPS.md).
    /// Adds three tables and two new columns:
    ///   • <c>operator_group</c>            — group definition (name + denormalized roster)
    ///   • <c>operator_group_candidate</c>  — normalized membership
    ///   • <c>operator_group_assignment</c> — device → group(s) (received from Main)
    ///   • <c>race_start_times.group_id</c>  — soft-link from heat to its starting group
    ///   • <c>processed_events.group_id</c>  — denormalized for per-group display counters
    ///
    /// Hand-written migration to stay consistent with the in-tree pattern (see comment in
    /// AddVoidedToProcessedEvents). Applied on startup by <c>PostgresInitService.MigrateAsync</c>.
    ///
    /// All additions are nullable / default-empty, so existing tests with no operator
    /// groups continue to function in legacy "all candidates visible" mode.
    /// </summary>
    public partial class AddOperatorGroups : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operator_group",
                columns: table => new
                {
                    group_id      = table.Column<Guid>(type: "uuid", nullable: false),
                    name          = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    candidate_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_group", x => x.group_id);
                });

            migrationBuilder.CreateTable(
                name: "operator_group_candidate",
                columns: table => new
                {
                    group_id     = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_group_candidate", x => new { x.group_id, x.candidate_id });
                    table.ForeignKey(
                        name: "FK_operator_group_candidate_operator_group_group_id",
                        column: x => x.group_id,
                        principalTable: "operator_group",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_operator_group_candidate_candidate",
                table: "operator_group_candidate",
                column: "candidate_id");

            migrationBuilder.CreateTable(
                name: "operator_group_assignment",
                columns: table => new
                {
                    group_id    = table.Column<Guid>(type: "uuid", nullable: false),
                    device_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_group_assignment", x => new { x.group_id, x.device_code });
                    table.ForeignKey(
                        name: "FK_operator_group_assignment_operator_group_group_id",
                        column: x => x.group_id,
                        principalTable: "operator_group",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_operator_group_assignment_device",
                table: "operator_group_assignment",
                column: "device_code");

            // race_start_times.group_id — descriptive only; finish-matching still keys on
            // candidate_ids array. Soft-link (no FK) to keep the table independent of
            // operator_group lifetimes — clearing operator_group via config refresh
            // shouldn't cascade-null an active heat's group_id, since the heat already
            // captured the relevant gun_start_time.
            migrationBuilder.AddColumn<Guid>(
                name: "group_id",
                table: "race_start_times",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_race_start_times_group_id",
                table: "race_start_times",
                column: "group_id");

            // processed_events.group_id — populated at finish-match time for fast
            // per-group counter queries on the display.
            migrationBuilder.AddColumn<Guid>(
                name: "group_id",
                table: "processed_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_processed_events_group_id",
                table: "processed_events",
                column: "group_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "idx_processed_events_group_id", table: "processed_events");
            migrationBuilder.DropColumn(name: "group_id", table: "processed_events");

            migrationBuilder.DropIndex(name: "idx_race_start_times_group_id", table: "race_start_times");
            migrationBuilder.DropColumn(name: "group_id", table: "race_start_times");

            migrationBuilder.DropTable(name: "operator_group_assignment");
            migrationBuilder.DropTable(name: "operator_group_candidate");
            migrationBuilder.DropTable(name: "operator_group");
        }
    }
}
