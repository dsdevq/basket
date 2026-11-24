using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AliasRejection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alias_rejection",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalized_text = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alias_rejection", x => x.id);
                    table.ForeignKey(
                        name: "FK_alias_rejection_product_product_id",
                        column: x => x.product_id,
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alias_rejection_normalized_text_product_id",
                table: "alias_rejection",
                columns: new[] { "normalized_text", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alias_rejection_product_id",
                table: "alias_rejection",
                column: "product_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alias_rejection");
        }
    }
}
