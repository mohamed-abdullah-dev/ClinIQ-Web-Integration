using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PatientAppointments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_PatientProfiles_Id_UserId",
                table: "PatientProfiles",
                columns: new[] { "Id", "UserId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_BookingSessions_Id_LocationId_DoctorProfileId_UserId",
                table: "BookingSessions",
                columns: new[] { "Id", "LocationId", "DoctorProfileId", "UserId" });

            migrationBuilder.CreateTable(
                name: "Appointments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClinicId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DoctorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Grade = table.Column<int>(type: "int", nullable: false),
                    SpecialtyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpecialtyAr = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SpecialtyEn = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PlaceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StartsLocal = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    EndsLocal = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BookingClosesAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Fee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    ContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContactPhone = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ReplacesAppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusBeforeCancellation = table.Column<int>(type: "int", nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CancellationCategory = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appointments", x => x.Id);
                    table.UniqueConstraint("AK_Appointments_Id_PatientId", x => new { x.Id, x.PatientId });
                    table.UniqueConstraint("AK_Appointments_Id_PatientId_DoctorProfileId", x => new { x.Id, x.PatientId, x.DoctorProfileId });
                    table.CheckConstraint("CK_Appointments_Cancellation", "([Status]=4 AND [CancelledByUserId] IS NOT NULL AND [CancelledAtUtc] IS NOT NULL AND [StatusBeforeCancellation] IS NOT NULL AND [StatusBeforeCancellation] IN (1,2) AND [CancellationReason] IS NOT NULL AND LEN([CancellationReason])>0 AND [CancellationCategory] IS NOT NULL) OR ([Status]<>4 AND [CancelledByUserId] IS NULL AND [CancelledAtUtc] IS NULL AND [StatusBeforeCancellation] IS NULL AND [CancellationReason] IS NULL AND [CancellationCategory] IS NULL)");
                    table.CheckConstraint("CK_Appointments_Selection", "[StartsAtUtc]<[EndsAtUtc] AND [StartsLocal]<[EndsLocal] AND [BookingClosesAtUtc]<=[EndsAtUtc] AND [Fee]>=0 AND [Currency]='EGP' AND [TimeZoneId]='Africa/Cairo' AND LEN([ContactName])>0 AND LEN([ContactPhone]) BETWEEN 7 AND 16");
                    table.CheckConstraint("CK_Appointments_Status", "[Status] BETWEEN 1 AND 7 AND ([ReplacesAppointmentId] IS NULL OR [ReplacesAppointmentId]<>[Id])");
                    table.ForeignKey(
                        name: "FK_Appointments_Appointments_ReplacesAppointmentId_PatientId_DoctorProfileId",
                        columns: x => new { x.ReplacesAppointmentId, x.PatientId, x.DoctorProfileId },
                        principalTable: "Appointments",
                        principalColumns: new[] { "Id", "PatientId", "DoctorProfileId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointments_AspNetUsers_CancelledByUserId",
                        column: x => x.CancelledByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointments_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointments_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointments_BookingSessions_SessionId_LocationId_DoctorProfileId_DoctorUserId",
                        columns: x => new { x.SessionId, x.LocationId, x.DoctorProfileId, x.DoctorUserId },
                        principalTable: "BookingSessions",
                        principalColumns: new[] { "Id", "LocationId", "DoctorProfileId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointments_PatientProfiles_PatientId_PatientUserId",
                        columns: x => new { x.PatientId, x.PatientUserId },
                        principalTable: "PatientProfiles",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    RequestHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ResultAppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentOperations", x => x.Id);
                    table.CheckConstraint("CK_AppointmentOperations_Kind", "[Kind] IN (1,2,3) AND LEN([RequestHash])=64");
                    table.ForeignKey(
                        name: "FK_AppointmentOperations_Appointments_ResultAppointmentId_PatientId",
                        columns: x => new { x.ResultAppointmentId, x.PatientId },
                        principalTable: "Appointments",
                        principalColumns: new[] { "Id", "PatientId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppointmentOperations_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppointmentOperations_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BookingNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    DeliveryStatus = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastAttemptUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingNotices", x => x.Id);
                    table.CheckConstraint("CK_BookingNotices_State", "[Kind] IN (1,2,3) AND [Attempts]>=0 AND [DeliveryStatus] IN ('Pending','Sending','DevelopmentFile','Unavailable')");
                    table.ForeignKey(
                        name: "FK_BookingNotices_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingNotices_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingNotices_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingNotices_AspNetUsers_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentOperations_CreatedByUserId",
                table: "AppointmentOperations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentOperations_LastModifiedByUserId",
                table: "AppointmentOperations",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentOperations_PatientId_RequestId",
                table: "AppointmentOperations",
                columns: new[] { "PatientId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentOperations_ResultAppointmentId_PatientId",
                table: "AppointmentOperations",
                columns: new[] { "ResultAppointmentId", "PatientId" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_CancelledByUserId",
                table: "Appointments",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_CreatedByUserId",
                table: "Appointments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_LastModifiedByUserId",
                table: "Appointments",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_PatientId_PatientUserId",
                table: "Appointments",
                columns: new[] { "PatientId", "PatientUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_PatientId_SessionId",
                table: "Appointments",
                columns: new[] { "PatientId", "SessionId" },
                unique: true,
                filter: "[Status] IN (1,2,3)");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_PatientId_StartsAtUtc_Id",
                table: "Appointments",
                columns: new[] { "PatientId", "StartsAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_ReplacesAppointmentId",
                table: "Appointments",
                column: "ReplacesAppointmentId",
                unique: true,
                filter: "[ReplacesAppointmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_ReplacesAppointmentId_PatientId_DoctorProfileId",
                table: "Appointments",
                columns: new[] { "ReplacesAppointmentId", "PatientId", "DoctorProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_SessionId_LocationId_DoctorProfileId_DoctorUserId",
                table: "Appointments",
                columns: new[] { "SessionId", "LocationId", "DoctorProfileId", "DoctorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_SessionId_Status",
                table: "Appointments",
                columns: new[] { "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingNotices_AppointmentId_RecipientUserId_Kind",
                table: "BookingNotices",
                columns: new[] { "AppointmentId", "RecipientUserId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingNotices_CreatedByUserId",
                table: "BookingNotices",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingNotices_DeliveryStatus_LastAttemptUtc",
                table: "BookingNotices",
                columns: new[] { "DeliveryStatus", "LastAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingNotices_LastModifiedByUserId",
                table: "BookingNotices",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingNotices_RecipientUserId",
                table: "BookingNotices",
                column: "RecipientUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM dbo.Appointments) THROW 51007, 'Patient appointment history must be retained; rollback is blocked.', 1;");
            migrationBuilder.DropTable(
                name: "AppointmentOperations");

            migrationBuilder.DropTable(
                name: "BookingNotices");

            migrationBuilder.DropTable(
                name: "Appointments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PatientProfiles_Id_UserId",
                table: "PatientProfiles");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_BookingSessions_Id_LocationId_DoctorProfileId_UserId",
                table: "BookingSessions");
        }
    }
}
