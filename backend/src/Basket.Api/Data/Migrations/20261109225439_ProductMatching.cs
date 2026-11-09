using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "confirmed_at",
                table: "store_listing",
                type: "timestamp with time zone",
                nullable: true);

            // Every listing so far was pasted by a person (the fleet could not link pages before this change), so each
            // is already a match someone chose.
            migrationBuilder.Sql("UPDATE store_listing SET confirmed_at = added_at;");

            migrationBuilder.AddColumn<string>(
                name: "barcode",
                table: "product",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "match_candidate",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    url = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    barcode = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: true),
                    basis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    proposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_candidate", x => x.id);
                    table.ForeignKey(
                        name: "FK_match_candidate_product_product_id",
                        column: x => x.product_id,
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_barcode",
                table: "product",
                column: "barcode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_match_candidate_product_id_store_url",
                table: "match_candidate",
                columns: new[] { "product_id", "store", "url" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "match_candidate");

            migrationBuilder.DropIndex(
                name: "IX_product_barcode",
                table: "product");

            migrationBuilder.DropColumn(
                name: "confirmed_at",
                table: "store_listing");

            migrationBuilder.DropColumn(
                name: "barcode",
                table: "product");
        }
    }
}
