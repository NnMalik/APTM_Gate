using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APTM.Gate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixSnakeCaseColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SourceClockOffsetMs",
                table: "race_start_times",
                newName: "source_clock_offset_ms");

            migrationBuilder.RenameColumn(
                name: "HeatNumber",
                table: "processed_events",
                newName: "heat_number");

            migrationBuilder.AlterColumn<int>(
                name: "source_clock_offset_ms",
                table: "race_start_times",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "source_clock_offset_ms",
                table: "race_start_times",
                newName: "SourceClockOffsetMs");

            migrationBuilder.RenameColumn(
                name: "heat_number",
                table: "processed_events",
                newName: "HeatNumber");

            migrationBuilder.AlterColumn<int>(
                name: "SourceClockOffsetMs",
                table: "race_start_times",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);
        }
    }
}
