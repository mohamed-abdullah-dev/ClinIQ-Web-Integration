using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DoctorResubmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DoctorRegistrations_UserId",
                table: "DoctorRegistrations");

            migrationBuilder.AddColumn<int>(
                name: "AttemptNumber",
                table: "DoctorRegistrations",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrent",
                table: "DoctorRegistrations",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreviousRegistrationId",
                table: "DoctorRegistrations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResubmittedByUserId",
                table: "DoctorRegistrations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_DoctorRegistrations_Id_UserId",
                table: "DoctorRegistrations",
                columns: new[] { "Id", "UserId" });

            migrationBuilder.CreateTable(
                name: "DoctorResubmissionGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecretHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SecurityStamp = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RegistrationVersion = table.Column<byte[]>(type: "binary(8)", fixedLength: true, maxLength: 8, nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorResubmissionGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DoctorResubmissionGrants_DoctorRegistrations_RegistrationId_UserId",
                        columns: x => new { x.RegistrationId, x.UserId },
                        principalTable: "DoctorRegistrations",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorRegistrations_PreviousRegistrationId",
                table: "DoctorRegistrations",
                column: "PreviousRegistrationId",
                unique: true,
                filter: "[PreviousRegistrationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorRegistrations_PreviousRegistrationId_UserId",
                table: "DoctorRegistrations",
                columns: new[] { "PreviousRegistrationId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorRegistrations_ResubmittedByUserId",
                table: "DoctorRegistrations",
                column: "ResubmittedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorRegistrations_UserId",
                table: "DoctorRegistrations",
                column: "UserId",
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorRegistrations_UserId_AttemptNumber",
                table: "DoctorRegistrations",
                columns: new[] { "UserId", "AttemptNumber" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_DoctorRegistrations_Attempt",
                table: "DoctorRegistrations",
                sql: "([AttemptNumber] = 1 AND [PreviousRegistrationId] IS NULL AND [ResubmittedByUserId] IS NULL) OR ([AttemptNumber] > 1 AND [PreviousRegistrationId] IS NOT NULL AND [ResubmittedByUserId] IS NOT NULL AND [ResubmittedByUserId] = [UserId])");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorResubmissionGrants_RegistrationId",
                table: "DoctorResubmissionGrants",
                column: "RegistrationId",
                unique: true,
                filter: "[ConsumedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorResubmissionGrants_RegistrationId_UserId",
                table: "DoctorResubmissionGrants",
                columns: new[] { "RegistrationId", "UserId" });

            migrationBuilder.AddForeignKey(
                name: "FK_DoctorRegistrations_AspNetUsers_ResubmittedByUserId",
                table: "DoctorRegistrations",
                column: "ResubmittedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DoctorRegistrations_DoctorRegistrations_PreviousRegistrationId_UserId",
                table: "DoctorRegistrations",
                columns: new[] { "PreviousRegistrationId", "UserId" },
                principalTable: "DoctorRegistrations",
                principalColumns: new[] { "Id", "UserId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [DoctorRegistrations] WHERE [AttemptNumber] > 1) THROW 51000, 'Cannot remove resubmission history. Use a forward migration.', 1;");
            migrationBuilder.DropForeignKey(
                name: "FK_DoctorRegistrations_AspNetUsers_ResubmittedByUserId",
                table: "DoctorRegistrations");

            migrationBuilder.DropForeignKey(
                name: "FK_DoctorRegistrations_DoctorRegistrations_PreviousRegistrationId_UserId",
                table: "DoctorRegistrations");

            migrationBuilder.DropTable(
                name: "DoctorResubmissionGrants");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_DoctorRegistrations_Id_UserId",
                table: "DoctorRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_DoctorRegistrations_PreviousRegistrationId",
                table: "DoctorRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_DoctorRegistrations_PreviousRegistrationId_UserId",
                table: "DoctorRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_DoctorRegistrations_ResubmittedByUserId",
                table: "DoctorRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_DoctorRegistrations_UserId",
                table: "DoctorRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_DoctorRegistrations_UserId_AttemptNumber",
                table: "DoctorRegistrations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DoctorRegistrations_Attempt",
                table: "DoctorRegistrations");

            migrationBuilder.DropColumn(
                name: "AttemptNumber",
                table: "DoctorRegistrations");

            migrationBuilder.DropColumn(
                name: "IsCurrent",
                table: "DoctorRegistrations");

            migrationBuilder.DropColumn(
                name: "PreviousRegistrationId",
                table: "DoctorRegistrations");

            migrationBuilder.DropColumn(
                name: "ResubmittedByUserId",
                table: "DoctorRegistrations");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorRegistrations_UserId",
                table: "DoctorRegistrations",
                column: "UserId",
                unique: true);
        }
    }
}
