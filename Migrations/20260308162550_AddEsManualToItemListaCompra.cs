using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EatHealthyCycle.Migrations
{
    /// <inheritdoc />
    public partial class AddEsManualToItemListaCompra : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsManual",
                table: "ItemsListaCompra",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EsManual",
                table: "ItemsListaCompra");
        }
    }
}
