using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OpenTrip : Migration
    {
        /// <summary>
        /// The open trip for lines already ticked when this migration runs: one trip with no close date and a record for each, bought
        /// at the shop the line was picked from (the plan's suggestion is not known back then). Public so a test runs the very
        /// statement the migration runs. Nothing is written when no line is ticked.
        /// </summary>
        public const string BackfillSql =
                """
                INSERT INTO trip (id, closed_at)
                SELECT gen_random_uuid(), NULL
                WHERE EXISTS (SELECT 1 FROM list_item WHERE is_checked)
                  AND NOT EXISTS (SELECT 1 FROM trip WHERE closed_at IS NULL);

                INSERT INTO trip_item (id, trip_id, text, checked_at, checked_by_user_id, product_id, store, bought_store, list_item_id)
                SELECT gen_random_uuid(), (SELECT id FROM trip WHERE closed_at IS NULL), l.text, l.checked_at, l.checked_by_user_id,
                       l.product_id, CASE WHEN l.product_id IS NULL THEN NULL ELSE l.store END,
                       CASE WHEN l.product_id IS NULL THEN NULL ELSE l.store END, l.id
                FROM list_item AS l
                WHERE l.is_checked
                  AND NOT EXISTS (SELECT 1 FROM trip_item AS t WHERE t.list_item_id = l.id);
                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bought_store",
                table: "trip_item",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "list_item_id",
                table: "trip_item",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "closed_at",
                table: "trip",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.CreateIndex(
                name: "IX_trip_item_list_item_id",
                table: "trip_item",
                column: "list_item_id",
                unique: true,
                filter: "list_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_trip_open",
                table: "trip",
                column: "closed_at",
                unique: true,
                filter: "closed_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "FK_trip_item_list_item_list_item_id",
                table: "trip_item",
                column: "list_item_id",
                principalTable: "list_item",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The open trip has no close date, which the old schema cannot hold; its items are still on the list as ticked lines.
            migrationBuilder.Sql("DELETE FROM trip WHERE closed_at IS NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_trip_item_list_item_list_item_id",
                table: "trip_item");

            migrationBuilder.DropIndex(
                name: "IX_trip_item_list_item_id",
                table: "trip_item");

            migrationBuilder.DropIndex(
                name: "ux_trip_open",
                table: "trip");

            migrationBuilder.DropColumn(
                name: "bought_store",
                table: "trip_item");

            migrationBuilder.DropColumn(
                name: "list_item_id",
                table: "trip_item");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "closed_at",
                table: "trip",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
