using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListItemQuantityNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "list_item",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "quantity",
                table: "list_item",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_list_item_quantity",
                table: "list_item",
                sql: "quantity IS NULL OR (quantity >= 1 AND quantity <= 99)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_list_item_quantity",
                table: "list_item");

            migrationBuilder.DropColumn(
                name: "note",
                table: "list_item");

            migrationBuilder.DropColumn(
                name: "quantity",
                table: "list_item");
        }
    }
}
