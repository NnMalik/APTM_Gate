using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APTM.Gate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventScopeToRaceData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "event_id",
                table: "raw_tag_buffer",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "event_id",
                table: "race_start_times",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "test_instance_id",
                table: "race_start_times",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_race_start_times_test_event",
                table: "race_start_times",
                columns: new[] { "test_instance_id", "event_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_race_start_times_test_event",
                table: "race_start_times");

            migrationBuilder.DropColumn(
                name: "event_id",
                table: "raw_tag_buffer");

            migrationBuilder.DropColumn(
                name: "event_id",
                table: "race_start_times");

            migrationBuilder.DropColumn(
                name: "test_instance_id",
                table: "race_start_times");
        }
    }
}
