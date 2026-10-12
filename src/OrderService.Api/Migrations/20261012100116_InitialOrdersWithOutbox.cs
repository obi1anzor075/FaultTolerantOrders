using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderService.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialOrdersWithOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Baseline used EnsureCreated, so these exact tables may exist without migration history.
            // Adopt that schema without dropping data; fresh databases get the same initial tables.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "Orders" (
                    "Id" uuid NOT NULL,
                    "UserId" uuid NOT NULL,
                    "Total" numeric(18,2) NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_Orders" PRIMARY KEY ("Id")
                );
                CREATE TABLE IF NOT EXISTS "OrderItems" (
                    "Id" uuid NOT NULL,
                    "OrderId" uuid NOT NULL,
                    "ProductId" uuid NOT NULL,
                    "Quantity" integer NOT NULL,
                    "UnitPrice" numeric(18,2) NOT NULL,
                    CONSTRAINT "PK_OrderItems" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_OrderItems_Orders_OrderId" FOREIGN KEY ("OrderId")
                        REFERENCES "Orders" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_OrderItems_OrderId" ON "OrderItems" ("OrderId");
                """);

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_NextRetryAt_OccurredAt",
                table: "OutboxMessages",
                columns: new[] { "NextRetryAt", "OccurredAt" },
                filter: "\"PublishedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_OrderId_Type",
                table: "OutboxMessages",
                columns: new[] { "OrderId", "Type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Orders and OrderItems may predate this migration; never delete adopted business data.
            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}
