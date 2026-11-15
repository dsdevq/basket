using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PullRunKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One nullable column, no data rewrite: the rows written so far stay null (their kind is unknown, and the catalog
            // health view skips them); new rows say whether they read the shelf's pages or swept a shop's whole catalog.
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "price_pull_run",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kind",
                table: "price_pull_run");
        }
    }
}
