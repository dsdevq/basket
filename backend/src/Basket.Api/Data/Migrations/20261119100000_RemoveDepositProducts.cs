using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDepositProducts : Migration
    {
        /// <summary>
        /// One-off cleanup of the bottle deposits earlier receipt pulls filed as products ("0.25 Deposit", "12 x 0.15 Deposit"): their
        /// lines on receipt trips, then the products (their paid prices and store listings go with them). Trips are not touched, so a
        /// receipt keeps the total printed on it. Public so a test runs the very statements the migration runs; running them again
        /// finds nothing to remove.
        /// </summary>
        public const string CleanupSql =
                """
                DELETE FROM trip_item AS i
                USING trip AS t
                WHERE i.trip_id = t.id
                  AND t.source = 'lidl_plus'
                  AND i.text ~* '\mdeposit\M';

                DELETE FROM product WHERE name ~* '\mdeposit\M';
                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(CleanupSql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
