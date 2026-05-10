using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APTM.Gate.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phase 7 of the Operator Groups feature — see DESIGN_OPERATOR_GROUPS.md §7. Adds
    /// two columns to <c>processed_events</c>:
    ///
    ///   • <c>heat_id</c>  — UUID copied from the matched <c>race_start_times.heat_id</c>
    ///     at finish-match time. Replaces <c>heat_number</c> as the load-bearing
    ///     identifier for void / completion / cancel queries. Across-group heat-number
    ///     collisions (Trainer-1's "heat 3" vs Trainer-2's "heat 3") were the root
    ///     cause of the cross-contamination bug those queries had.
    ///   • <c>status</c>   — VARCHAR(20). Currently only "UNRESOLVED" is meaningful;
    ///     NULL means matched / not applicable. The buffer processor's old
    ///     <c>raceStarts[0]</c> fallback for unmatched finishes is replaced by writing
    ///     a row with <c>status = 'UNRESOLVED', heat_id = NULL, group_id = NULL</c>
    ///     for human review.
    ///
    /// Both columns are nullable. Existing processed_events rows are unaffected and
    /// stay matched in display queries (status NULL = OK). No data migration needed.
    /// </summary>
    public partial class AddHeatIdAndStatusToProcessedEvents : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "heat_id",
                table: "processed_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_processed_events_heat_id",
                table: "processed_events",
                column: "heat_id");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "processed_events",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "idx_processed_events_heat_id", table: "processed_events");
            migrationBuilder.DropColumn(name: "heat_id", table: "processed_events");
            migrationBuilder.DropColumn(name: "status", table: "processed_events");
        }
    }
}
