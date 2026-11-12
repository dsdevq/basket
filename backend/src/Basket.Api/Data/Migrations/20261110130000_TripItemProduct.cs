using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TripItemProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "product_id",
                table: "trip_item",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "store",
                table: "trip_item",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_trip_item_product_id",
                table: "trip_item",
                column: "product_id");

            migrationBuilder.AddForeignKey(
                name: "FK_trip_item_product_product_id",
                table: "trip_item",
                column: "product_id",
                principalTable: "product",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_trip_item_product_product_id",
                table: "trip_item");

            migrationBuilder.DropIndex(
                name: "IX_trip_item_product_id",
                table: "trip_item");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "trip_item");

            migrationBuilder.DropColumn(
                name: "store",
                table: "trip_item");
        }
    }
}
