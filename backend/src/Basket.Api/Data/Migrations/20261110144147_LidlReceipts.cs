using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LidlReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_paid_price_product_id_store_paid_on",
                table: "paid_price");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paid_at",
                table: "paid_price",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_id",
                table: "trip",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "trip",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "total",
                table: "trip",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_paid_price_product_id_store_paid_on",
                table: "paid_price",
                columns: new[] { "product_id", "store", "paid_on" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_trip_source_external_id",
                table: "trip",
                columns: new[] { "source", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_paid_price_product_id_store_paid_on",
                table: "paid_price");

            migrationBuilder.DropIndex(
                name: "IX_trip_source_external_id",
                table: "trip");

            migrationBuilder.DropColumn(
                name: "paid_at",
                table: "paid_price");

            migrationBuilder.DropColumn(
                name: "external_id",
                table: "trip");

            migrationBuilder.DropColumn(
                name: "source",
                table: "trip");

            migrationBuilder.DropColumn(
                name: "total",
                table: "trip");

            migrationBuilder.CreateIndex(
                name: "IX_paid_price_product_id_store_paid_on",
                table: "paid_price",
                columns: new[] { "product_id", "store", "paid_on" });
        }
    }
}
