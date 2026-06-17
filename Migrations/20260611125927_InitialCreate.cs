using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendorInvoiceAssistant.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Vendors and Invoices tables already exist in the database — skipped.

            migrationBuilder.CreateTable(
                name: "ConversationContexts",
                columns: table => new
                {
                    PhoneNumber = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    LastInvoiceNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationContexts", x => x.PhoneNumber);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationContexts");

            migrationBuilder.DropTable(
                name: "Invoices");

            migrationBuilder.DropTable(
                name: "Vendors");
        }
    }
}
