using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APTM.Gate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGateIdentityName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "gate_identity",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "name",
                table: "gate_identity");
        }
    }
}
