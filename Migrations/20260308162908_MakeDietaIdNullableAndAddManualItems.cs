using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EatHealthyCycle.Migrations
{
    /// <inheritdoc />
    public partial class MakeDietaIdNullableAndAddManualItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlanesSemanal_Dietas_DietaId",
                table: "PlanesSemanal");

            migrationBuilder.AlterColumn<int>(
                name: "DietaId",
                table: "PlanesSemanal",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddForeignKey(
                name: "FK_PlanesSemanal_Dietas_DietaId",
                table: "PlanesSemanal",
                column: "DietaId",
                principalTable: "Dietas",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlanesSemanal_Dietas_DietaId",
                table: "PlanesSemanal");

            migrationBuilder.AlterColumn<int>(
                name: "DietaId",
                table: "PlanesSemanal",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PlanesSemanal_Dietas_DietaId",
                table: "PlanesSemanal",
                column: "DietaId",
                principalTable: "Dietas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
