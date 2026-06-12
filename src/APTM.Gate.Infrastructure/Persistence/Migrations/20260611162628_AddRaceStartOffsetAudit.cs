using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APTM.Gate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRaceStartOffsetAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "applied_offset_ms",
                table: "race_start_times",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offset_method",
                table: "race_start_times",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "applied_offset_ms",
                table: "race_start_times");

            migrationBuilder.DropColumn(
                name: "offset_method",
                table: "race_start_times");
        }
    }
}
