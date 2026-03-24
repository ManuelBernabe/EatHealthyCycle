using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EatHealthyCycle.Migrations
{
    /// <inheritdoc />
    public partial class AddKcalToAlimento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kcal",
                table: "Alimentos",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kcal",
                table: "Alimentos");
        }
    }
}
