using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dispatch.Data.MySql.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    key_id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_bin"),
                    key_hash = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "utf8mb4_bin"),
                    name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false, collation: "utf8mb4_bin"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "UTC_TIMESTAMP()"),
                    last_used_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    message_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    revoked = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    revoked_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    rate_limit_per_minute = table.Column<int>(type: "int", nullable: false, defaultValue: 100),
                    scope = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, defaultValue: "send", collation: "utf8mb4_bin")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_bin");

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    logged_at = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "UTC_TIMESTAMP()"),
                    kind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_bin"),
                    category = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_bin"),
                    @event = table.Column<string>(name: "event", type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin"),
                    severity = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "Info", collation: "utf8mb4_bin"),
                    actor = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_bin"),
                    source_ip = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_bin"),
                    detail = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_bin");

            migrationBuilder.CreateTable(
                name: "config",
                columns: table => new
                {
                    key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin"),
                    value = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_bin"),
                    encrypted = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "UTC_TIMESTAMP()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_config", x => x.key);
                })
                .Annotation("Relational:Collation", "utf8mb4_bin");

            migrationBuilder.CreateTable(
                name: "config_smtp_credentials",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    username = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false, collation: "utf8mb4_bin"),
                    password_hash = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "utf8mb4_bin"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "UTC_TIMESTAMP()"),
                    last_used_at = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_config_smtp_credentials", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_bin");

            migrationBuilder.CreateTable(
                name: "relay_counters",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    relay_id = table.Column<int>(type: "int", nullable: false),
                    received = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    delivered = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    failed = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    retried = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    denied = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relay_counters", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_bin");

            migrationBuilder.CreateTable(
                name: "relays",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin"),
                    provider = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    is_default = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    enabled = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: true),
                    max_concurrency = table.Column<int>(type: "int", nullable: false, defaultValue: 4),
                    max_message_bytes = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "UTC_TIMESTAMP()"),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "UTC_TIMESTAMP()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relays", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_bin");

            migrationBuilder.CreateTable(
                name: "routing_rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    priority = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin"),
                    recipient_pattern = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_bin"),
                    sender_pattern = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_bin"),
                    relay_id = table.Column<int>(type: "int", nullable: false),
                    enabled = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "UTC_TIMESTAMP()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routing_rules", x => x.id);
                    table.ForeignKey(
                        name: "FK_routing_rules_relays_relay_id",
                        column: x => x.relay_id,
                        principalTable: "relays",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Relational:Collation", "utf8mb4_bin");

            migrationBuilder.CreateTable(
                name: "relay_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    logged_at = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "UTC_TIMESTAMP()"),
                    spool_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    @event = table.Column<string>(name: "event", type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_bin"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_bin"),
                    retry_attempt = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    from_address = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "utf8mb4_bin"),
                    from_domain = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    to_addresses = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_bin"),
                    to_domain = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_bin"),
                    subject = table.Column<string>(type: "varchar(998)", maxLength: 998, nullable: false, collation: "utf8mb4_bin"),
                    size_bytes = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    relay_id = table.Column<int>(type: "int", nullable: true),
                    relay_name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_bin"),
                    routing_rule_id = table.Column<int>(type: "int", nullable: true),
                    routing_rule_name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_bin"),
                    routing_matched = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    provider = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_bin"),
                    provider_message_id = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_bin"),
                    provider_response = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin"),
                    duration_ms = table.Column<int>(type: "int", nullable: true),
                    error = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin"),
                    ingest_source = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "SMTP", collation: "utf8mb4_bin"),
                    source_ip = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_bin"),
                    api_key_id = table.Column<int>(type: "int", nullable: true),
                    api_key_name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_bin"),
                    tags = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin"),
                    x_mailer = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_bin"),
                    attachment_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relay_log", x => x.id);
                    table.ForeignKey(
                        name: "FK_relay_log_api_keys_api_key_id",
                        column: x => x.api_key_id,
                        principalTable: "api_keys",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_relay_log_relays_relay_id",
                        column: x => x.relay_id,
                        principalTable: "relays",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_relay_log_routing_rules_routing_rule_id",
                        column: x => x.routing_rule_id,
                        principalTable: "routing_rules",
                        principalColumn: "id");
                })
                .Annotation("Relational:Collation", "utf8mb4_bin");

            migrationBuilder.CreateIndex(
                name: "UQ_api_keys_key_id",
                table: "api_keys",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_at",
                table: "audit_log",
                columns: new[] { "logged_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_kind",
                table: "audit_log",
                columns: new[] { "kind", "logged_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_config_smtp_credentials_username",
                table: "config_smtp_credentials",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_relay_counters_date",
                table: "relay_counters",
                column: "date",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "UQ_relay_counters",
                table: "relay_counters",
                columns: new[] { "date", "relay_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_api_key",
                table: "relay_log",
                columns: new[] { "api_key_id", "logged_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_from_domain",
                table: "relay_log",
                columns: new[] { "from_domain", "logged_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_purge",
                table: "relay_log",
                column: "logged_at");

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_relay",
                table: "relay_log",
                columns: new[] { "relay_id", "logged_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_rule",
                table: "relay_log",
                columns: new[] { "routing_rule_id", "logged_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_source",
                table: "relay_log",
                columns: new[] { "ingest_source", "logged_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_spool_id",
                table: "relay_log",
                columns: new[] { "spool_id", "logged_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_status_date",
                table: "relay_log",
                columns: new[] { "status", "logged_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_to_domain",
                table: "relay_log",
                columns: new[] { "to_domain", "logged_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_relays_name",
                table: "relays",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_routing_rules_priority",
                table: "routing_rules",
                column: "priority",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_routing_rules_relay_id",
                table: "routing_rules",
                column: "relay_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "config");

            migrationBuilder.DropTable(
                name: "config_smtp_credentials");

            migrationBuilder.DropTable(
                name: "relay_counters");

            migrationBuilder.DropTable(
                name: "relay_log");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "routing_rules");

            migrationBuilder.DropTable(
                name: "relays");
        }
    }
}
