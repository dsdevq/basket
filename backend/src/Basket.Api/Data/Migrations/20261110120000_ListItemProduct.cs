using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListItemProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "product_id",
                table: "list_item",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "store",
                table: "list_item",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_list_item_product_id_store",
                table: "list_item",
                columns: new[] { "product_id", "store" },
                unique: true,
                filter: "is_checked = false AND product_id IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "FK_list_item_product_product_id",
                table: "list_item",
                column: "product_id",
                principalTable: "product",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_list_item_product_product_id",
                table: "list_item");

            migrationBuilder.DropIndex(
                name: "IX_list_item_product_id_store",
                table: "list_item");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "list_item");

            migrationBuilder.DropColumn(
                name: "store",
                table: "list_item");
        }
    }
}
