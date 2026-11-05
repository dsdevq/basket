using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PriceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "price_pull_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    store = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    listings = table.Column<int>(type: "integer", nullable: false),
                    read = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_pull_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_users_added_by_user_id",
                        column: x => x.added_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "paid_price",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: false),
                    price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paid_price", x => x.id);
                    table.ForeignKey(
                        name: "FK_paid_price_product_product_id",
                        column: x => x.product_id,
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_listing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    url = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_listing", x => x.id);
                    table.ForeignKey(
                        name: "FK_store_listing_product_product_id",
                        column: x => x.product_id,
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "price_observation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("PK_price_observation", x => x.id);
                    table.ForeignKey(
                        name: "FK_price_observation_store_listing_listing_id",
                        column: x => x.listing_id,
                        principalTable: "store_listing",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_paid_price_product_id_store_paid_on",
                table: "paid_price",
                columns: new[] { "product_id", "store", "paid_on" });

            migrationBuilder.CreateIndex(
                name: "IX_price_observation_listing_id_observed_on",
                table: "price_observation",
                columns: new[] { "listing_id", "observed_on" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_price_pull_run_store_finished_at",
                table: "price_pull_run",
                columns: new[] { "store", "finished_at" });

            migrationBuilder.CreateIndex(
                name: "IX_product_added_by_user_id",
                table: "product",
                column: "added_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_store_listing_product_id_store",
                table: "store_listing",
                columns: new[] { "product_id", "store" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paid_price");

            migrationBuilder.DropTable(
                name: "price_observation");

            migrationBuilder.DropTable(
                name: "price_pull_run");

            migrationBuilder.DropTable(
                name: "store_listing");

            migrationBuilder.DropTable(
                name: "product");
        }
    }
}
