using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace APTM.Gate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candidates",
                columns: table => new
                {
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    gender = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    candidate_type_id = table.Column<int>(type: "integer", nullable: false),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: false),
                    jacket_number = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidates", x => x.candidate_id);
                });

            migrationBuilder.CreateTable(
                name: "checkpoint_config",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    route_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    checkpoint_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkpoint_config", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gate_config",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    test_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    test_instance_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    gate_role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    checkpoint_sequence = table.Column<int>(type: "integer", nullable: true),
                    scheduled_date = table.Column<DateOnly>(type: "date", nullable: false),
                    data_snapshot_version = table.Column<int>(type: "integer", nullable: false),
                    clock_offset_ms = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gate_config", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "race_start_times",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    heat_id = table.Column<Guid>(type: "uuid", nullable: false),
                    heat_number = table.Column<int>(type: "integer", nullable: false),
                    gun_start_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_race_start_times", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "raw_tag_buffer",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tag_epc = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    read_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    antenna_port = table.Column<int>(type: "integer", nullable: true),
                    rssi = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "PENDING"),
                    is_duplicate = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    inserted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_tag_buffer", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "received_sync_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    client_record_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_device_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    data_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_received_sync_data", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scoring_types",
                columns: table => new
                {
                    scoring_type_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scoring_types", x => x.scoring_type_id);
                });

            migrationBuilder.CreateTable(
                name: "sync_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    puller_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    puller_device_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    last_processed_event_id = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    last_received_sync_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pulled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tag_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_epc = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag_assignments", x => x.id);
                    table.ForeignKey(
                        name: "FK_tag_assignments_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "candidates",
                        principalColumn: "candidate_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "processed_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_epc = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    read_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_seconds = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: true),
                    checkpoint_sequence = table.Column<int>(type: "integer", nullable: true),
                    is_first_read = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    raw_buffer_id = table.Column<long>(type: "bigint", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_processed_events_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "candidates",
                        principalColumn: "candidate_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_processed_events_raw_tag_buffer_raw_buffer_id",
                        column: x => x.raw_buffer_id,
                        principalTable: "raw_tag_buffer",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "scoring_statuses",
                columns: table => new
                {
                    scoring_status_id = table.Column<int>(type: "integer", nullable: false),
                    scoring_type_id = table.Column<int>(type: "integer", nullable: false),
                    status_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status_label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    is_passing_status = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scoring_statuses", x => x.scoring_status_id);
                    table.ForeignKey(
                        name: "FK_scoring_statuses_scoring_types_scoring_type_id",
                        column: x => x.scoring_type_id,
                        principalTable: "scoring_types",
                        principalColumn: "scoring_type_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "test_events",
                columns: table => new
                {
                    test_type_event_id = table.Column<int>(type: "integer", nullable: false),
                    event_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    event_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    event_id = table.Column<int>(type: "integer", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    scoring_type_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_events", x => x.test_type_event_id);
                    table.ForeignKey(
                        name: "FK_test_events_scoring_types_scoring_type_id",
                        column: x => x.scoring_type_id,
                        principalTable: "scoring_types",
                        principalColumn: "scoring_type_id");
                });

            migrationBuilder.CreateIndex(
                name: "idx_processed_events_candidate",
                table: "processed_events",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "idx_processed_events_type",
                table: "processed_events",
                column: "event_type");

            migrationBuilder.CreateIndex(
                name: "IX_processed_events_raw_buffer_id",
                table: "processed_events",
                column: "raw_buffer_id");

            migrationBuilder.CreateIndex(
                name: "IX_race_start_times_heat_id",
                table: "race_start_times",
                column: "heat_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_raw_tag_buffer_status",
                table: "raw_tag_buffer",
                column: "status",
                filter: "status = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "idx_received_sync_client_record",
                table: "received_sync_data",
                column: "client_record_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scoring_statuses_scoring_type_id",
                table: "scoring_statuses",
                column: "scoring_type_id");

            migrationBuilder.CreateIndex(
                name: "idx_tag_assignments_epc",
                table: "tag_assignments",
                column: "tag_epc",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tag_assignments_candidate_id",
                table: "tag_assignments",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "IX_test_events_scoring_type_id",
                table: "test_events",
                column: "scoring_type_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "checkpoint_config");

            migrationBuilder.DropTable(
                name: "gate_config");

            migrationBuilder.DropTable(
                name: "processed_events");

            migrationBuilder.DropTable(
                name: "race_start_times");

            migrationBuilder.DropTable(
                name: "received_sync_data");

            migrationBuilder.DropTable(
                name: "scoring_statuses");

            migrationBuilder.DropTable(
                name: "sync_log");

            migrationBuilder.DropTable(
                name: "tag_assignments");

            migrationBuilder.DropTable(
                name: "test_events");

            migrationBuilder.DropTable(
                name: "raw_tag_buffer");

            migrationBuilder.DropTable(
                name: "candidates");

            migrationBuilder.DropTable(
                name: "scoring_types");
        }
    }
}
