using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Trips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trip",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trip", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "trip_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    checked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trip_item", x => x.id);
                    table.ForeignKey(
                        name: "FK_trip_item_trip_trip_id",
                        column: x => x.trip_id,
                        principalTable: "trip",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_trip_item_users_checked_by_user_id",
                        column: x => x.checked_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trip_closed_at",
                table: "trip",
                column: "closed_at");

            migrationBuilder.CreateIndex(
                name: "IX_trip_item_checked_by_user_id",
                table: "trip_item",
                column: "checked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_trip_item_trip_id",
                table: "trip_item",
                column: "trip_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trip_item");

            migrationBuilder.DropTable(
                name: "trip");
        }
    }
}
