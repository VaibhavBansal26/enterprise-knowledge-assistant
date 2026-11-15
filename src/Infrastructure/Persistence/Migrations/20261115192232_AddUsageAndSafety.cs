using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageAndSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "grounding_score",
                table: "chat_messages",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "low_grounding",
                table: "chat_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "tenant_quotas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monthly_token_limit = table.Column<long>(type: "bigint", nullable: true),
                    monthly_cost_limit_usd = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_quotas", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "usage_ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(14,6)", precision: 14, scale: 6, nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    time_to_first_token_ms = table.Column<int>(type: "integer", nullable: true),
                    grounding_score = table.Column<double>(type: "double precision", nullable: true),
                    retrieved_passages = table.Column<int>(type: "integer", nullable: true),
                    neutralized = table.Column<int>(type: "integer", nullable: false),
                    pii_redactions = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_ledger", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_quotas_tenant_id",
                table: "tenant_quotas",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_ledger_tenant_id_created_at",
                table: "usage_ledger",
                columns: new[] { "tenant_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_quotas");

            migrationBuilder.DropTable(
                name: "usage_ledger");

            migrationBuilder.DropColumn(
                name: "grounding_score",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "low_grounding",
                table: "chat_messages");
        }
    }
}
