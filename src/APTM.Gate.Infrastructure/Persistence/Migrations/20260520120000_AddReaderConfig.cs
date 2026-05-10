using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APTM.Gate.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>reader_config</c> — a single-row override table for the UHF reader's network
    /// settings (host, port, power, filter, reconnect delay). When a row exists it overrides
    /// the <c>Reader:*</c> values from appsettings; absent it, the worker falls back to
    /// appsettings (no breaking change for existing NUCs that haven't called the new endpoint
    /// yet). Editable at runtime via PUT /gate/reader/settings — picked up on the next reconnect.
    ///
    /// Hand-written migration (no snapshot regen) to stay consistent with prior in-tree
    /// migrations (see AddVoidedToProcessedEvents). EF picks up Up()/Down() at runtime via
    /// reflection so this applies cleanly via PostgresInitService.MigrateAsync at startup.
    /// </summary>
    public partial class AddReaderConfig : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reader_config",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    host = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    default_power = table.Column<int>(type: "integer", nullable: false),
                    epc_filter_bits = table.Column<int>(type: "integer", nullable: false),
                    reconnect_delay_ms = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reader_config", x => x.id);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reader_config");
        }
    }
}
