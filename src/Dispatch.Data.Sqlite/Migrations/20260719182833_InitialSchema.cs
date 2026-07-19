using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dispatch.Data.Sqlite.Migrations
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
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    key_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    key_hash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    message_count = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    revoked = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    rate_limit_per_minute = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 100),
                    scope = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "send")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    logged_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    @event = table.Column<string>(name: "event", type: "TEXT", maxLength: 128, nullable: false),
                    severity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "Info"),
                    actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    source_ip = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    detail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "config",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false),
                    encrypted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_config", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "config_smtp_credentials",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_config_smtp_credentials", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "relay_counters",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    relay_id = table.Column<int>(type: "INTEGER", nullable: false),
                    received = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    delivered = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    failed = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    retried = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    denied = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relay_counters", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "relays",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    is_default = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    max_concurrency = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 4),
                    max_message_bytes = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relays", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "routing_rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    recipient_pattern = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    sender_pattern = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    relay_id = table.Column<int>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "relay_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    logged_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    spool_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    @event = table.Column<string>(name: "event", type: "TEXT", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    retry_attempt = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    from_address = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    from_domain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    to_addresses = table.Column<string>(type: "TEXT", nullable: false),
                    to_domain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    subject = table.Column<string>(type: "TEXT", maxLength: 998, nullable: false),
                    size_bytes = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    relay_id = table.Column<int>(type: "INTEGER", nullable: true),
                    relay_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    routing_rule_id = table.Column<int>(type: "INTEGER", nullable: true),
                    routing_rule_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    routing_matched = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    provider_message_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    provider_response = table.Column<string>(type: "TEXT", nullable: true),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                    ingest_source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "SMTP"),
                    source_ip = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    api_key_id = table.Column<int>(type: "INTEGER", nullable: true),
                    api_key_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    tags = table.Column<string>(type: "TEXT", nullable: true),
                    x_mailer = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    attachment_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
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
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_key_id",
                table: "api_keys",
                column: "key_id",
                unique: true,
                filter: "NOT revoked");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_at",
                table: "audit_log",
                columns: new[] { "logged_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_kind",
                table: "audit_log",
                columns: new[] { "kind", "logged_at" });

            migrationBuilder.CreateIndex(
                name: "IX_config_smtp_credentials_username",
                table: "config_smtp_credentials",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_relay_counters_date",
                table: "relay_counters",
                column: "date");

            migrationBuilder.CreateIndex(
                name: "UQ_relay_counters",
                table: "relay_counters",
                columns: new[] { "date", "relay_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_api_key",
                table: "relay_log",
                columns: new[] { "api_key_id", "logged_at" },
                filter: "api_key_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_from_domain",
                table: "relay_log",
                columns: new[] { "from_domain", "logged_at" });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_purge",
                table: "relay_log",
                column: "logged_at");

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_relay",
                table: "relay_log",
                columns: new[] { "relay_id", "logged_at" });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_rule",
                table: "relay_log",
                columns: new[] { "routing_rule_id", "logged_at" });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_source",
                table: "relay_log",
                columns: new[] { "ingest_source", "logged_at" });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_spool_id",
                table: "relay_log",
                columns: new[] { "spool_id", "logged_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_status_date",
                table: "relay_log",
                columns: new[] { "status", "logged_at" });

            migrationBuilder.CreateIndex(
                name: "IX_relay_log_to_domain",
                table: "relay_log",
                columns: new[] { "to_domain", "logged_at" });

            migrationBuilder.CreateIndex(
                name: "IX_relays_default",
                table: "relays",
                column: "is_default",
                unique: true,
                filter: "is_default");

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
