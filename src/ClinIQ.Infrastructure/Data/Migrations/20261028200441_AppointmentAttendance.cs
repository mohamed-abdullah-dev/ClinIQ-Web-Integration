using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppointmentAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM dbo.Appointments WHERE Status IN (2,3,7)) THROW 51010, 'Existing attendance outcomes require factual migration review; timestamps will not be invented.', 1;");
            migrationBuilder.DropIndex(
                name: "IX_Appointments_PatientId_SessionId",
                table: "Appointments");

            migrationBuilder.AddColumn<int>(
                name: "LastQueueNumber",
                table: "BookingSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "OperationalBranchId",
                table: "BookingSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArrivedAtUtc",
                table: "Appointments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttendanceRevision",
                table: "Appointments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckInRecordedAtUtc",
                table: "Appointments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAtUtc",
                table: "Appointments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletionRecordedAtUtc",
                table: "Appointments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LateAuthorizedAtUtc",
                table: "Appointments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LateAuthorizedByUserId",
                table: "Appointments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QueueNumber",
                table: "Appointments",
                type: "int",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Appointments_Id_SessionId_DoctorProfileId_PatientId",
                table: "Appointments",
                columns: new[] { "Id", "SessionId", "DoctorProfileId", "PatientId" });

            migrationBuilder.CreateTable(
                name: "AttendanceRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PreviousStatus = table.Column<int>(type: "int", nullable: false),
                    NewStatus = table.Column<int>(type: "int", nullable: false),
                    BeforeFactsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    AfterFactsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    PreviousVersion = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceRecords", x => x.Id);
                    table.CheckConstraint("CK_AttendanceRecords_Facts", "[Revision]>0 AND [Action] BETWEEN 1 AND 6 AND [PreviousStatus] BETWEEN 1 AND 7 AND [NewStatus] BETWEEN 1 AND 7 AND LEN([Reason])>0 AND ISJSON([BeforeFactsJson])=1 AND ISJSON([AfterFactsJson])=1");
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_Appointments_AppointmentId_SessionId_DoctorProfileId_PatientId",
                        columns: x => new { x.AppointmentId, x.SessionId, x.DoctorProfileId, x.PatientId },
                        principalTable: "Appointments",
                        principalColumns: new[] { "Id", "SessionId", "DoctorProfileId", "PatientId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingSessions_OperationalBranchId",
                table: "BookingSessions",
                column: "OperationalBranchId");

            migrationBuilder.Sql("EXEC(N'ALTER TABLE [BookingSessions] ADD CONSTRAINT [CK_BookingSessions_Queue] CHECK ([LastQueueNumber]>=0);');");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_LateAuthorizedByUserId",
                table: "Appointments",
                column: "LateAuthorizedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_PatientId_SessionId",
                table: "Appointments",
                columns: new[] { "PatientId", "SessionId" },
                unique: true,
                filter: "[Status] IN (1,2,3,6,7)");

            migrationBuilder.Sql("EXEC(N'CREATE UNIQUE INDEX [IX_Appointments_SessionId_QueueNumber] ON [Appointments] ([SessionId],[QueueNumber]) WHERE [QueueNumber] IS NOT NULL;');");

            migrationBuilder.Sql("EXEC(N'ALTER TABLE [Appointments] ADD CONSTRAINT [CK_Appointments_Attendance] CHECK (([QueueNumber] IS NULL OR [QueueNumber]>0) AND (([ArrivedAtUtc] IS NULL AND [CheckInRecordedAtUtc] IS NULL) OR ([ArrivedAtUtc] IS NOT NULL AND [CheckInRecordedAtUtc] IS NOT NULL AND [QueueNumber] IS NOT NULL AND [ArrivedAtUtc]>=[StartsAtUtc] AND [ArrivedAtUtc]<=[CheckInRecordedAtUtc])) AND ([Status] NOT IN (2,3,7) OR [ArrivedAtUtc] IS NOT NULL) AND ([Status] NOT IN (1,5,6) OR [ArrivedAtUtc] IS NULL) AND (([Status]=3 AND [CompletedAtUtc] IS NOT NULL AND [CompletionRecordedAtUtc] IS NOT NULL AND [ArrivedAtUtc] IS NOT NULL AND [CompletedAtUtc]>=[ArrivedAtUtc] AND [CompletedAtUtc]<=[CompletionRecordedAtUtc]) OR ([Status]<>3 AND [CompletedAtUtc] IS NULL AND [CompletionRecordedAtUtc] IS NULL)) AND (([LateAuthorizedByUserId] IS NULL AND [LateAuthorizedAtUtc] IS NULL) OR ([LateAuthorizedByUserId] IS NOT NULL AND [LateAuthorizedAtUtc] IS NOT NULL AND [ArrivedAtUtc] IS NOT NULL AND [LateAuthorizedAtUtc]>=[ArrivedAtUtc])) AND ([ArrivedAtUtc] IS NULL OR [ArrivedAtUtc]<[EndsAtUtc] OR [LateAuthorizedByUserId] IS NOT NULL) AND ([LateAuthorizedByUserId] IS NULL OR [LateAuthorizedByUserId]=[DoctorUserId]));');");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_ActorUserId",
                table: "AttendanceRecords",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_AppointmentId_PreviousVersion",
                table: "AttendanceRecords",
                columns: new[] { "AppointmentId", "PreviousVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_AppointmentId_RecordedAtUtc_Id",
                table: "AttendanceRecords",
                columns: new[] { "AppointmentId", "RecordedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_AppointmentId_Revision",
                table: "AttendanceRecords",
                columns: new[] { "AppointmentId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_AppointmentId_SessionId_DoctorProfileId_PatientId",
                table: "AttendanceRecords",
                columns: new[] { "AppointmentId", "SessionId", "DoctorProfileId", "PatientId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Appointments_AspNetUsers_LateAuthorizedByUserId",
                table: "Appointments",
                column: "LateAuthorizedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BookingSessions_ClinicBranches_OperationalBranchId",
                table: "BookingSessions",
                column: "OperationalBranchId",
                principalTable: "ClinicBranches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // Linking independent places is not implemented yet: current associations are the original scopes.
            migrationBuilder.Sql("EXEC(N'UPDATE s SET OperationalBranchId=p.ClinicBranchId FROM dbo.BookingSessions s JOIN dbo.DoctorPracticeLocations p ON p.Id=s.LocationId WHERE p.ClinicBranchId IS NOT NULL;');");
            // EXEC gives CREATE TRIGGER its own batch in both EF execution and a generated SQL script.
            migrationBuilder.Sql("EXEC(N'CREATE TRIGGER dbo.TR_AttendanceRecords_Immutable ON dbo.AttendanceRecords INSTEAD OF UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 51009, ''Attendance history is append-only.'', 1; END;');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM dbo.Appointments) THROW 51008, 'Attendance and appointment history must be retained. Use a forward migration.', 1;");
            migrationBuilder.DropForeignKey(
                name: "FK_Appointments_AspNetUsers_LateAuthorizedByUserId",
                table: "Appointments");

            migrationBuilder.DropForeignKey(
                name: "FK_BookingSessions_ClinicBranches_OperationalBranchId",
                table: "BookingSessions");

            migrationBuilder.DropTable(
                name: "AttendanceRecords");

            migrationBuilder.DropIndex(
                name: "IX_BookingSessions_OperationalBranchId",
                table: "BookingSessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BookingSessions_Queue",
                table: "BookingSessions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Appointments_Id_SessionId_DoctorProfileId_PatientId",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_LateAuthorizedByUserId",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_PatientId_SessionId",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_SessionId_QueueNumber",
                table: "Appointments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Appointments_Attendance",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "LastQueueNumber",
                table: "BookingSessions");

            migrationBuilder.DropColumn(
                name: "OperationalBranchId",
                table: "BookingSessions");

            migrationBuilder.DropColumn(
                name: "ArrivedAtUtc",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "AttendanceRevision",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "CheckInRecordedAtUtc",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "CompletionRecordedAtUtc",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "LateAuthorizedAtUtc",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "LateAuthorizedByUserId",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "QueueNumber",
                table: "Appointments");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_PatientId_SessionId",
                table: "Appointments",
                columns: new[] { "PatientId", "SessionId" },
                unique: true,
                filter: "[Status] IN (1,2,3)");
        }
    }
}
