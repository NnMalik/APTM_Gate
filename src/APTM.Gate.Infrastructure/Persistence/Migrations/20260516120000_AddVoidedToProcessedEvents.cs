using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APTM.Gate.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>voided</c> to <c>processed_events</c> so HHT-issued
    /// <c>race_cancel</c> and <c>heat_candidate_remove</c> pushes can
    /// retroactively invalidate finish events without losing the audit trail.
    ///
    /// New rows default to <c>false</c>; existing rows are back-filled to
    /// <c>false</c> (every pre-existing finish was a real, valid one). The
    /// partial index <c>idx_processed_events_active</c> keeps the hot-path
    /// queries (display feed + dedup) bounded to live rows.
    ///
    /// NOTE: GateDbContextModelSnapshot is currently out-of-date with prior
    /// schema changes (HeatCompletions table is in the entity model but not
    /// in the snapshot). This migration is therefore written manually to
    /// avoid noise from a snapshot regen — running
    /// <c>dotnet ef migrations add</c> against this codebase would also
    /// re-add unrelated tables. EF picks up Up()/Down() at runtime via
    /// reflection, so the migration applies cleanly without a snapshot diff.
    /// </summary>
    public partial class AddVoidedToProcessedEvents : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "voided",
                table: "processed_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "idx_processed_events_active",
                table: "processed_events",
                column: "voided",
                filter: "voided = false");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_processed_events_active",
                table: "processed_events");

            migrationBuilder.DropColumn(
                name: "voided",
                table: "processed_events");
        }
    }
}
