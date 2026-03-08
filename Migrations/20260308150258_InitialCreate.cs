using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EatHealthyCycle.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Usuarios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    RefreshToken = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RefreshTokenExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActivationToken = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ActivationTokenExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    TwoFactorSecret = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RecoveryCodes = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usuarios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dietas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UsuarioId = table.Column<int>(type: "INTEGER", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", nullable: true),
                    FechaImportacion = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ArchivoOriginal = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dietas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dietas_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RegistrosPeso",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UsuarioId = table.Column<int>(type: "INTEGER", nullable: false),
                    Fecha = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Peso = table.Column<decimal>(type: "TEXT", precision: 5, scale: 2, nullable: false),
                    Nota = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrosPeso", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegistrosPeso_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DietaDias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DietaId = table.Column<int>(type: "INTEGER", nullable: false),
                    DiaSemana = table.Column<int>(type: "INTEGER", nullable: false),
                    Nota = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DietaDias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DietaDias_Dietas_DietaId",
                        column: x => x.DietaId,
                        principalTable: "Dietas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanesSemanal",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UsuarioId = table.Column<int>(type: "INTEGER", nullable: false),
                    DietaId = table.Column<int>(type: "INTEGER", nullable: false),
                    FechaInicio = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FechaFin = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanesSemanal", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanesSemanal_Dietas_DietaId",
                        column: x => x.DietaId,
                        principalTable: "Dietas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlanesSemanal_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Comidas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DietaDiaId = table.Column<int>(type: "INTEGER", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    Orden = table.Column<int>(type: "INTEGER", nullable: false),
                    Nota = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comidas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Comidas_DietaDias_DietaDiaId",
                        column: x => x.DietaDiaId,
                        principalTable: "DietaDias",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ItemsListaCompra",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanSemanalId = table.Column<int>(type: "INTEGER", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<string>(type: "TEXT", nullable: true),
                    Categoria = table.Column<string>(type: "TEXT", nullable: true),
                    Comprado = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemsListaCompra", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemsListaCompra_PlanesSemanal_PlanSemanalId",
                        column: x => x.PlanSemanalId,
                        principalTable: "PlanesSemanal",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanDias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanSemanalId = table.Column<int>(type: "INTEGER", nullable: false),
                    Fecha = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DiaSemana = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanDias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanDias_PlanesSemanal_PlanSemanalId",
                        column: x => x.PlanSemanalId,
                        principalTable: "PlanesSemanal",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Alimentos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComidaId = table.Column<int>(type: "INTEGER", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<string>(type: "TEXT", nullable: true),
                    Categoria = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alimentos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alimentos_Comidas_ComidaId",
                        column: x => x.ComidaId,
                        principalTable: "Comidas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanComidas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanDiaId = table.Column<int>(type: "INTEGER", nullable: false),
                    ComidaId = table.Column<int>(type: "INTEGER", nullable: true),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", nullable: false),
                    Completada = table.Column<bool>(type: "INTEGER", nullable: false),
                    FechaCompletada = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanComidas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanComidas_Comidas_ComidaId",
                        column: x => x.ComidaId,
                        principalTable: "Comidas",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PlanComidas_PlanDias_PlanDiaId",
                        column: x => x.PlanDiaId,
                        principalTable: "PlanDias",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Usuarios",
                columns: new[] { "Id", "ActivationToken", "ActivationTokenExpiresAt", "Email", "FechaCreacion", "IsActive", "Nombre", "PasswordHash", "RecoveryCodes", "RefreshToken", "RefreshTokenExpiresAt", "Role", "TwoFactorSecret", "Username" },
                values: new object[] { 1, null, null, "admin@eathealthycycle.local", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Admin", "$2a$11$r1zN2HmMy2FnebH4onffcOzWj8IsqmrB0Yxe5k1VgbPzXOh29WGDm", null, null, null, 3, null, "admin" });

            migrationBuilder.CreateIndex(
                name: "IX_Alimentos_ComidaId",
                table: "Alimentos",
                column: "ComidaId");

            migrationBuilder.CreateIndex(
                name: "IX_Comidas_DietaDiaId",
                table: "Comidas",
                column: "DietaDiaId");

            migrationBuilder.CreateIndex(
                name: "IX_DietaDias_DietaId",
                table: "DietaDias",
                column: "DietaId");

            migrationBuilder.CreateIndex(
                name: "IX_Dietas_UsuarioId",
                table: "Dietas",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemsListaCompra_PlanSemanalId",
                table: "ItemsListaCompra",
                column: "PlanSemanalId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanComidas_ComidaId",
                table: "PlanComidas",
                column: "ComidaId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanComidas_PlanDiaId",
                table: "PlanComidas",
                column: "PlanDiaId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanDias_PlanSemanalId",
                table: "PlanDias",
                column: "PlanSemanalId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanesSemanal_DietaId",
                table: "PlanesSemanal",
                column: "DietaId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanesSemanal_UsuarioId",
                table: "PlanesSemanal",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosPeso_UsuarioId_Fecha",
                table: "RegistrosPeso",
                columns: new[] { "UsuarioId", "Fecha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_Email",
                table: "Usuarios",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_Username",
                table: "Usuarios",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alimentos");

            migrationBuilder.DropTable(
                name: "ItemsListaCompra");

            migrationBuilder.DropTable(
                name: "PlanComidas");

            migrationBuilder.DropTable(
                name: "RegistrosPeso");

            migrationBuilder.DropTable(
                name: "Comidas");

            migrationBuilder.DropTable(
                name: "PlanDias");

            migrationBuilder.DropTable(
                name: "DietaDias");

            migrationBuilder.DropTable(
                name: "PlanesSemanal");

            migrationBuilder.DropTable(
                name: "Dietas");

            migrationBuilder.DropTable(
                name: "Usuarios");
        }
    }
}
