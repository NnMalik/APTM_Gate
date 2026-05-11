using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APTM.Gate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatorGroupsAndGateIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_processed_events_candidates_candidate_id",
                table: "processed_events");

            migrationBuilder.DropForeignKey(
                name: "FK_processed_events_raw_tag_buffer_raw_buffer_id",
                table: "processed_events");

            migrationBuilder.AddColumn<Guid>(
                name: "group_id",
                table: "race_start_times",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "candidate_id",
                table: "processed_events",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "group_id",
                table: "processed_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "heat_id",
                table: "processed_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "processed_events",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "voided",
                table: "processed_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "gate_identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    checkpoint_sequence = table.Column<int>(type: "integer", nullable: true),
                    device_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    set_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gate_identity", x => x.id);
                    table.CheckConstraint("ck_gate_identity_checkpoint_sequence", "(role = 'Checkpoint' AND checkpoint_sequence IS NOT NULL) OR (role <> 'Checkpoint' AND checkpoint_sequence IS NULL)");
                    table.CheckConstraint("ck_gate_identity_role", "role IN ('Start', 'Checkpoint', 'Finish')");
                    table.CheckConstraint("ck_gate_identity_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "heat_completions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    heat_id = table.Column<Guid>(type: "uuid", nullable: false),
                    heat_number = table.Column<int>(type: "integer", nullable: false),
                    expected_count = table.Column<int>(type: "integer", nullable: false),
                    finished_count = table.Column<int>(type: "integer", nullable: false),
                    last_candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closure_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "auto"),
                    source_device_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_heat_completions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "operator_group",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    candidate_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_group", x => x.group_id);
                });

            migrationBuilder.CreateTable(
                name: "operator_group_assignment",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_group_assignment", x => new { x.group_id, x.device_code });
                });

            migrationBuilder.CreateTable(
                name: "operator_group_candidate",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_group_candidate", x => new { x.group_id, x.candidate_id });
                });

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

            migrationBuilder.CreateIndex(
                name: "idx_race_start_times_group_id",
                table: "race_start_times",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "idx_processed_events_active",
                table: "processed_events",
                column: "voided",
                filter: "voided = false");

            migrationBuilder.CreateIndex(
                name: "idx_processed_events_group_id",
                table: "processed_events",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "idx_processed_events_heat_id",
                table: "processed_events",
                column: "heat_id");

            migrationBuilder.CreateIndex(
                name: "idx_heat_completions_heat_id",
                table: "heat_completions",
                column: "heat_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_operator_group_assignment_device",
                table: "operator_group_assignment",
                column: "device_code");

            migrationBuilder.CreateIndex(
                name: "idx_operator_group_candidate_candidate",
                table: "operator_group_candidate",
                column: "candidate_id");

            migrationBuilder.AddForeignKey(
                name: "FK_processed_events_candidates_candidate_id",
                table: "processed_events",
                column: "candidate_id",
                principalTable: "candidates",
                principalColumn: "candidate_id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_processed_events_raw_tag_buffer_raw_buffer_id",
                table: "processed_events",
                column: "raw_buffer_id",
                principalTable: "raw_tag_buffer",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_processed_events_candidates_candidate_id",
                table: "processed_events");

            migrationBuilder.DropForeignKey(
                name: "FK_processed_events_raw_tag_buffer_raw_buffer_id",
                table: "processed_events");

            migrationBuilder.DropTable(
                name: "gate_identity");

            migrationBuilder.DropTable(
                name: "heat_completions");

            migrationBuilder.DropTable(
                name: "operator_group");

            migrationBuilder.DropTable(
                name: "operator_group_assignment");

            migrationBuilder.DropTable(
                name: "operator_group_candidate");

            migrationBuilder.DropTable(
                name: "reader_config");

            migrationBuilder.DropIndex(
                name: "idx_race_start_times_group_id",
                table: "race_start_times");

            migrationBuilder.DropIndex(
                name: "idx_processed_events_active",
                table: "processed_events");

            migrationBuilder.DropIndex(
                name: "idx_processed_events_group_id",
                table: "processed_events");

            migrationBuilder.DropIndex(
                name: "idx_processed_events_heat_id",
                table: "processed_events");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "race_start_times");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "processed_events");

            migrationBuilder.DropColumn(
                name: "heat_id",
                table: "processed_events");

            migrationBuilder.DropColumn(
                name: "status",
                table: "processed_events");

            migrationBuilder.DropColumn(
                name: "voided",
                table: "processed_events");

            migrationBuilder.AlterColumn<Guid>(
                name: "candidate_id",
                table: "processed_events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_processed_events_candidates_candidate_id",
                table: "processed_events",
                column: "candidate_id",
                principalTable: "candidates",
                principalColumn: "candidate_id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_processed_events_raw_tag_buffer_raw_buffer_id",
                table: "processed_events",
                column: "raw_buffer_id",
                principalTable: "raw_tag_buffer",
                principalColumn: "id");
        }
    }
}
