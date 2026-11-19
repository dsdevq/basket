using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AldiSweepCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One nullable column, no data rewrite: the Aldi sweep keeps where it stopped here, so the next night starts there.
            migrationBuilder.AddColumn<int>(
                name: "next_offset",
                table: "price_pull_run",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "next_offset",
                table: "price_pull_run");
        }
    }
}
