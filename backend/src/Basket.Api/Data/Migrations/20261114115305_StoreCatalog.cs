using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "store_product_id",
                table: "store_listing",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "store_product",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    store = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    external_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    url = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    size_text = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    size_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    size_unit = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    size_packs = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    unit_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    unit_price_basis = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    barcode = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: true),
                    category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    image_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    discontinued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "english")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "name", "brand" })
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_product", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "store_product_price",
                columns: table => new
                {
                    store_product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    was_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    loyalty_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    loyalty_scheme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    promo_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    in_stock = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_product_price", x => new { x.store_product_id, x.observed_on });
                    table.ForeignKey(
                        name: "FK_store_product_price_store_product_store_product_id",
                        column: x => x.store_product_id,
                        principalTable: "store_product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_store_listing_store_product_id",
                table: "store_listing",
                column: "store_product_id");

            migrationBuilder.CreateIndex(
                name: "IX_store_product_barcode",
                table: "store_product",
                column: "barcode",
                filter: "barcode IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_store_product_search_vector",
                table: "store_product",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_store_product_store_external_id",
                table: "store_product",
                columns: new[] { "store", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_store_product_store_last_seen_at",
                table: "store_product",
                columns: new[] { "store", "last_seen_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_store_listing_store_product_store_product_id",
                table: "store_listing",
                column: "store_product_id",
                principalTable: "store_product",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_store_listing_store_product_store_product_id",
                table: "store_listing");

            migrationBuilder.DropTable(
                name: "store_product_price");

            migrationBuilder.DropTable(
                name: "store_product");

            migrationBuilder.DropIndex(
                name: "IX_store_listing_store_product_id",
                table: "store_listing");

            migrationBuilder.DropColumn(
                name: "store_product_id",
                table: "store_listing");
        }
    }
}
