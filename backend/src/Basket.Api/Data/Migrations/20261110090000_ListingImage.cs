using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListingImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "image_url",
                table: "store_listing",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "image_url",
                table: "store_listing");
        }
    }
}
