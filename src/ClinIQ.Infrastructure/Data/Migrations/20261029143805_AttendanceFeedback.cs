using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AttendanceFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_AttendanceRecords_Id_AppointmentId_Revision",
                table: "AttendanceRecords",
                columns: new[] { "Id", "AppointmentId", "Revision" });

            migrationBuilder.CreateTable(
                name: "AttendanceFeedbackRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceAttendanceRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevision = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: true),
                    ResponseRevision = table.Column<int>(type: "int", nullable: false),
                    RespondedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewState = table.Column<int>(type: "int", nullable: true),
                    Finding = table.Column<int>(type: "int", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewedResponseRevision = table.Column<int>(type: "int", nullable: true),
                    ReviewedAppointmentVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceFeedbackRequests", x => x.Id);
                    table.UniqueConstraint("AK_AttendanceFeedbackRequests_Id_PatientUserId", x => new { x.Id, x.PatientUserId });
                    table.CheckConstraint("CK_AttendanceFeedbackRequests_Answer", "([ResponseRevision]=0 AND [Outcome] IS NULL AND [RespondedAtUtc] IS NULL AND [ReviewState] IS NULL) OR ([ResponseRevision]>0 AND [Outcome] IS NOT NULL AND [Outcome] IN (1,2,3) AND [RespondedAtUtc] IS NOT NULL AND [RespondedAtUtc]>=[CreatedAtUtc] AND [RespondedAtUtc]<[ExpiresAtUtc] AND ([ReviewState] IS NOT NULL OR [Outcome]=3))");
                    table.CheckConstraint("CK_AttendanceFeedbackRequests_Review", "(([ReviewState] IS NULL OR [ReviewState]=1) AND [Finding] IS NULL AND [ReviewedByUserId] IS NULL AND [ReviewedAtUtc] IS NULL AND [ReviewReason] IS NULL AND [ReviewedResponseRevision] IS NULL AND [ReviewedAppointmentVersion] IS NULL) OR ([ReviewState] IS NOT NULL AND [ReviewState] IN (2,3) AND [ReviewedByUserId] IS NOT NULL AND [ReviewedAtUtc] IS NOT NULL AND [ReviewedAtUtc]>=[RespondedAtUtc] AND [ReviewReason] IS NOT NULL AND LEN([ReviewReason])>0 AND [ReviewedResponseRevision] IS NOT NULL AND [ReviewedResponseRevision]=[ResponseRevision] AND [ReviewedAppointmentVersion] IS NOT NULL AND (([ReviewState]=2 AND [Finding] IS NOT NULL AND [Finding] IN (1,2,3)) OR ([ReviewState]=3 AND [Finding] IS NULL)))");
                    table.CheckConstraint("CK_AttendanceFeedbackRequests_Window", "[SourceRevision]>0 AND [ExpiresAtUtc]=DATEADD(day,7,[CreatedAtUtc])");
                    table.ForeignKey(
                        name: "FK_AttendanceFeedbackRequests_Appointments_AppointmentId_PatientId",
                        columns: x => new { x.AppointmentId, x.PatientId },
                        principalTable: "Appointments",
                        principalColumns: new[] { "Id", "PatientId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceFeedbackRequests_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceFeedbackRequests_AttendanceRecords_SourceAttendanceRecordId_AppointmentId_SourceRevision",
                        columns: x => new { x.SourceAttendanceRecordId, x.AppointmentId, x.SourceRevision },
                        principalTable: "AttendanceRecords",
                        principalColumns: new[] { "Id", "AppointmentId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceFeedbackRequests_PatientProfiles_PatientId_PatientUserId",
                        columns: x => new { x.PatientId, x.PatientUserId },
                        principalTable: "PatientProfiles",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceFeedbackResponses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FeedbackRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsEdit = table.Column<bool>(type: "bit", nullable: false),
                    PreviousVersion = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceFeedbackResponses", x => x.Id);
                    table.CheckConstraint("CK_AttendanceFeedbackResponses_Facts", "[Revision]>0 AND [Outcome] IN (1,2,3) AND (([Revision]=1 AND [IsEdit]=0) OR ([Revision]>1 AND [IsEdit]=1))");
                    table.ForeignKey(
                        name: "FK_AttendanceFeedbackResponses_AttendanceFeedbackRequests_FeedbackRequestId_ActorUserId",
                        columns: x => new { x.FeedbackRequestId, x.ActorUserId },
                        principalTable: "AttendanceFeedbackRequests",
                        principalColumns: new[] { "Id", "PatientUserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceFeedbackRequests_AppointmentId_PatientId",
                table: "AttendanceFeedbackRequests",
                columns: new[] { "AppointmentId", "PatientId" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceFeedbackRequests_PatientId_PatientUserId",
                table: "AttendanceFeedbackRequests",
                columns: new[] { "PatientId", "PatientUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceFeedbackRequests_PatientUserId_CreatedAtUtc_Id",
                table: "AttendanceFeedbackRequests",
                columns: new[] { "PatientUserId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceFeedbackRequests_ReviewedByUserId",
                table: "AttendanceFeedbackRequests",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceFeedbackRequests_ReviewState_CreatedAtUtc_Id",
                table: "AttendanceFeedbackRequests",
                columns: new[] { "ReviewState", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceFeedbackRequests_SourceAttendanceRecordId",
                table: "AttendanceFeedbackRequests",
                column: "SourceAttendanceRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceFeedbackRequests_SourceAttendanceRecordId_AppointmentId_SourceRevision",
                table: "AttendanceFeedbackRequests",
                columns: new[] { "SourceAttendanceRecordId", "AppointmentId", "SourceRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceFeedbackResponses_FeedbackRequestId_ActorUserId",
                table: "AttendanceFeedbackResponses",
                columns: new[] { "FeedbackRequestId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceFeedbackResponses_FeedbackRequestId_Revision",
                table: "AttendanceFeedbackResponses",
                columns: new[] { "FeedbackRequestId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceFeedbackResponses_FeedbackRequestId_SubmissionId",
                table: "AttendanceFeedbackResponses",
                columns: new[] { "FeedbackRequestId", "SubmissionId" },
                unique: true);

            // Keep the original event timestamp: upgrading must not silently restart an old response window.
            migrationBuilder.Sql("""
                INSERT INTO AttendanceFeedbackRequests
                    (Id,AppointmentId,PatientId,PatientUserId,SourceAttendanceRecordId,SourceRevision,CreatedAtUtc,ExpiresAtUtc,ResponseRevision)
                SELECT NEWID(), a.Id, a.PatientId, a.PatientUserId, r.Id, r.Revision, r.RecordedAtUtc, DATEADD(day,7,r.RecordedAtUtc), 0
                FROM AttendanceRecords r JOIN Appointments a ON a.Id=r.AppointmentId
                WHERE r.NewStatus=6 AND r.PreviousStatus<>6;
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER TR_AttendanceFeedbackResponses_Immutable ON AttendanceFeedbackResponses AFTER UPDATE, DELETE AS
                BEGIN SET NOCOUNT ON; THROW 51012, ''Feedback response history is immutable.'', 1; END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER TR_AttendanceFeedbackRequests_Protect ON AttendanceFeedbackRequests AFTER INSERT, UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM deleted WHERE ReviewState IN (2,3)) OR EXISTS(
                        SELECT Id,AppointmentId,PatientId,PatientUserId,SourceAttendanceRecordId,SourceRevision,CreatedAtUtc,ExpiresAtUtc FROM deleted
                        EXCEPT SELECT Id,AppointmentId,PatientId,PatientUserId,SourceAttendanceRecordId,SourceRevision,CreatedAtUtc,ExpiresAtUtc FROM inserted)
                        THROW 51011, ''Feedback origin and finalized decisions are protected.'', 1;
                    IF EXISTS(SELECT 1 FROM inserted i JOIN AttendanceRecords r ON r.Id=i.SourceAttendanceRecordId
                        WHERE r.NewStatus<>6 OR r.PreviousStatus=6 OR i.CreatedAtUtc<>r.RecordedAtUtc)
                        THROW 51011, ''Feedback requires its original staff NoShow event timestamp.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS(SELECT 1 FROM Appointments) OR EXISTS(SELECT 1 FROM AttendanceFeedbackRequests)
                    THROW 51014, 'Refusing to remove feedback support from recorded appointments or erase patient feedback.', 1;
                DROP TRIGGER TR_AttendanceFeedbackRequests_Protect;
                DROP TRIGGER TR_AttendanceFeedbackResponses_Immutable;
                """);
            migrationBuilder.DropTable(
                name: "AttendanceFeedbackResponses");

            migrationBuilder.DropTable(
                name: "AttendanceFeedbackRequests");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AttendanceRecords_Id_AppointmentId_Revision",
                table: "AttendanceRecords");
        }
    }
}
