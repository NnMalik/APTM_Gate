using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APTM.Gate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "event_id",
                table: "processed_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "active_event_id",
                table: "gate_config",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "active_event_name",
                table: "gate_config",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_processed_events_event_id",
                table: "processed_events",
                column: "event_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_processed_events_event_id",
                table: "processed_events");

            migrationBuilder.DropColumn(
                name: "event_id",
                table: "processed_events");

            migrationBuilder.DropColumn(
                name: "active_event_id",
                table: "gate_config");

            migrationBuilder.DropColumn(
                name: "active_event_name",
                table: "gate_config");
        }
    }
}
