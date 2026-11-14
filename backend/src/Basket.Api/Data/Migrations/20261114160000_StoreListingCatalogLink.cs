using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Basket.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreListingCatalogLink : Migration
    {
        /// <summary>The fill, public so a test runs the very statement the migration runs.</summary>
        public const string LinkSql =
                """
                UPDATE store_listing AS l
                SET store_product_id = p.id
                FROM store_product AS p
                WHERE l.store_product_id IS NULL
                  AND p.store = l.store
                  AND p.external_id = CASE l.store
                      WHEN 'Lidl' THEN substring(l.url from '/p(\d+)$')
                      WHEN 'Tesco' THEN substring(l.url from '/products/(\d+)$')
                      ELSE substring(l.url from '-(\d+)$')
                  END;
                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data only, no schema change. Fills store_listing.store_product_id for the pages already on the shelf: the shop's own
            // product id is in the page's address (Lidl "p<number>", Tesco "/products/<id>", Aldi and Dunnes the trailing number;
            // see Stores.ProductId) and is the catalog's external_id. Only unlinked listings are touched, a page whose product
            // is not in the catalog yet is left null (a sweep or a relink links it later), and with no catalog rows it updates
            // nothing, so it is safe to run on any database and again.
            migrationBuilder.Sql(LinkSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The links are derived from the addresses and can be filled again; nothing to undo.
        }
    }
}
