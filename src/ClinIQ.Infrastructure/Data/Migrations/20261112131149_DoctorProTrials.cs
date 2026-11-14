using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DoctorProTrials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DoctorProTrials",
                columns: table => new
                {
                    DoctorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TermsVersion = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorProTrials", x => x.DoctorProfileId);
                    table.CheckConstraint("CK_DoctorProTrials_UtcDuration", "DATEPART(TZOFFSET, [ActivatedAtUtc]) = 0 AND DATEPART(TZOFFSET, [ExpiresAtUtc]) = 0 AND [ExpiresAtUtc] = DATEADD(day, 30, [ActivatedAtUtc])");
                    table.ForeignKey(
                        name: "FK_DoctorProTrials_DoctorProfiles_DoctorProfileId",
                        column: x => x.DoctorProfileId,
                        principalTable: "DoctorProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER dbo.TR_DoctorProTrials_Immutable ON dbo.DoctorProTrials AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 51024, ''A recorded doctor trial cannot be rewritten or deleted.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM dbo.DoctorProTrials) THROW 51025, 'Cannot remove recorded doctor trials.', 1;");
            migrationBuilder.DropTable(
                name: "DoctorProTrials");
        }
    }
}
