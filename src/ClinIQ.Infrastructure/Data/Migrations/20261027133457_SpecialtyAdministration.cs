using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpecialtyAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                table: "Specialties",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateTable(
                name: "SpecialtyCatalogChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpecialtyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    PreviousNameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PreviousNameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecialtyCatalogChanges", x => x.Id);
                    table.CheckConstraint("CK_SpecialtyCatalogChanges_Operation", "[Operation] IN (1,2,3)");
                    table.ForeignKey(
                        name: "FK_SpecialtyCatalogChanges_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpecialtyCatalogChanges_ActorUserId",
                table: "SpecialtyCatalogChanges",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SpecialtyCatalogChanges_SpecialtyId_OccurredAtUtc",
                table: "SpecialtyCatalogChanges",
                columns: new[] { "SpecialtyId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpecialtyCatalogChanges");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Specialties");
        }
    }
}
