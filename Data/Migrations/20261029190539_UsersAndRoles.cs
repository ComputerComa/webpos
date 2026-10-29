using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WebPos.Data.Migrations
{
    /// <summary>
    /// Sales are now made by signed-in users instead of a cashier picked from a list. Existing sales keep their
    /// cashier's name (copied into CashierName) but no longer link to anyone, and the Cashiers table is dropped.
    /// </summary>
    public partial class UsersAndRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Copy each sale's cashier name before the Cashiers table goes.
            migrationBuilder.AddColumn<string>(
                name: "CashierName",
                table: "Transactions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE "Transactions" t SET "CashierName" = c."Name" FROM "Cashiers" c WHERE c."Id" = t."CashierId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Cashiers_CashierId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_CashierId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "CashierId",
                table: "Transactions");

            migrationBuilder.DropTable(
                name: "Cashiers");

            // The cashier is now a user account; null for the sales copied above.
            migrationBuilder.AddColumn<string>(
                name: "CashierId",
                table: "Transactions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_CashierId",
                table: "Transactions",
                column: "CashierId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_AspNetUsers_CashierId",
                table: "Transactions",
                column: "CashierId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddColumn<string>(
                name: "VoidedBy",
                table: "Transactions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CatalogPrice",
                table: "TransactionItems",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceOverriddenBy",
                table: "TransactionItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CardHash",
                table: "AspNetUsers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_CardHash",
                table: "AspNetUsers",
                column: "CardHash",
                unique: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "AspNetUsers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE "AspNetUsers" SET "DisplayName" = left(coalesce("UserName", ''), 100);
                """);

            // Existing accounts (the superadmin) stay active. The model has no database default,
            // so EF always writes the value it holds.
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql("""
                ALTER TABLE "AspNetUsers" ALTER COLUMN "IsActive" DROP DEFAULT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_AspNetUsers_CashierId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_CashierId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "CashierId",
                table: "Transactions");

            // Rebuild the cashier list from the names on past sales.
            migrationBuilder.CreateTable(
                name: "Cashiers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cashiers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cashiers_Name",
                table: "Cashiers",
                column: "Name",
                unique: true);

            migrationBuilder.Sql("""
                UPDATE "Transactions" SET "CashierName" = 'Unknown' WHERE "CashierName" = '';
                INSERT INTO "Cashiers" ("Name", "IsActive") SELECT DISTINCT "CashierName", true FROM "Transactions";
                """);

            migrationBuilder.AddColumn<int>(
                name: "CashierId",
                table: "Transactions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE "Transactions" t SET "CashierId" = c."Id" FROM "Cashiers" c WHERE c."Name" = t."CashierName";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_CashierId",
                table: "Transactions",
                column: "CashierId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_Cashiers_CashierId",
                table: "Transactions",
                column: "CashierId",
                principalTable: "Cashiers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropColumn(
                name: "CashierName",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "VoidedBy",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "CatalogPrice",
                table: "TransactionItems");

            migrationBuilder.DropColumn(
                name: "PriceOverriddenBy",
                table: "TransactionItems");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_CardHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "CardHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "AspNetUsers");
        }
    }
}
