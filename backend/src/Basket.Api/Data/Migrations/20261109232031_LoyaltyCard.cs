using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LoyaltyCard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "loyalty_card",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    store = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    number_protected = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_card", x => x.id);
                    table.ForeignKey(
                        name: "FK_loyalty_card_users_added_by_user_id",
                        column: x => x.added_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_card_added_by_user_id",
                table: "loyalty_card",
                column: "added_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_card_store",
                table: "loyalty_card",
                column: "store",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loyalty_card");
        }
    }
}
