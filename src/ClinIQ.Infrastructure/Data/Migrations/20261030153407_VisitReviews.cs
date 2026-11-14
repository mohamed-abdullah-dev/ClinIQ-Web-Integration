using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class VisitReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF;");
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Appointments_Id_PatientId_DoctorProfileId_LocationId_PatientUserId",
                table: "Appointments",
                columns: new[] { "Id", "PatientId", "DoctorProfileId", "LocationId", "PatientUserId" });

            migrationBuilder.CreateTable(
                name: "VisitReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Stars = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EditedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Hidden = table.Column<bool>(type: "bit", nullable: false),
                    ModerationCategory = table.Column<int>(type: "int", nullable: true),
                    ModerationReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ModeratedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModeratedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitReviews", x => x.Id);
                    table.CheckConstraint("CK_VisitReviews_Facts", "[Stars] BETWEEN 1 AND 5 AND [Revision]>0 AND (([Hidden]=1 AND [ModerationCategory] IS NOT NULL AND [ModerationCategory] IN (1,2,3) AND [ModeratedByUserId] IS NOT NULL AND [ModeratedAtUtc] IS NOT NULL AND [ModerationReason] IS NOT NULL AND LEN([ModerationReason])>0) OR ([Hidden]=0 AND [ModerationCategory] IS NULL))");
                    table.ForeignKey(
                        name: "FK_VisitReviews_Appointments_AppointmentId_PatientId_DoctorProfileId_LocationId_PatientUserId",
                        columns: x => new { x.AppointmentId, x.PatientId, x.DoctorProfileId, x.LocationId, x.PatientUserId },
                        principalTable: "Appointments",
                        principalColumns: new[] { "Id", "PatientId", "DoctorProfileId", "LocationId", "PatientUserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VisitReviews_AspNetUsers_ModeratedByUserId",
                        column: x => x.ModeratedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VisitReviewAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", maxLength: 32000, nullable: false),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", maxLength: 32000, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AppointmentVersion = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitReviewAudits", x => x.Id);
                    table.CheckConstraint("CK_VisitReviewAudits_Facts", "[Revision]>0 AND [Action] IN ('Create','Edit','Hide','Restore') AND ISJSON([BeforeJson])=1 AND ISJSON([AfterJson])=1");
                    table.ForeignKey(
                        name: "FK_VisitReviewAudits_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VisitReviewAudits_VisitReviews_ReviewId",
                        column: x => x.ReviewId,
                        principalTable: "VisitReviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisitReviewAudits_ActorUserId",
                table: "VisitReviewAudits",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_VisitReviewAudits_ReviewId_Revision",
                table: "VisitReviewAudits",
                columns: new[] { "ReviewId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VisitReviews_AppointmentId",
                table: "VisitReviews",
                column: "AppointmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VisitReviews_AppointmentId_PatientId_DoctorProfileId_LocationId_PatientUserId",
                table: "VisitReviews",
                columns: new[] { "AppointmentId", "PatientId", "DoctorProfileId", "LocationId", "PatientUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitReviews_DoctorProfileId_Hidden_CreatedAtUtc_Id",
                table: "VisitReviews",
                columns: new[] { "DoctorProfileId", "Hidden", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitReviews_ModeratedByUserId",
                table: "VisitReviews",
                column: "ModeratedByUserId");

            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER TR_VisitReviewAudits_Immutable ON VisitReviewAudits
                AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51016, ''Visit-review history is immutable.'', 1;
                END;');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM VisitReviews) OR EXISTS (SELECT 1 FROM VisitReviewAudits)
                    THROW 51017, 'Visit-review rollback requires an explicit reviewed data-preservation plan.', 1;
                """);
            migrationBuilder.DropTable(
                name: "VisitReviewAudits");

            migrationBuilder.DropTable(
                name: "VisitReviews");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Appointments_Id_PatientId_DoctorProfileId_LocationId_PatientUserId",
                table: "Appointments");
        }
    }
}
