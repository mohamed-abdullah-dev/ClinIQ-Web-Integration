using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DoctorScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_DoctorPracticeLocations_Id_DoctorProfileId_UserId",
                table: "DoctorPracticeLocations",
                columns: new[] { "Id", "DoctorProfileId", "UserId" });

            migrationBuilder.CreateTable(
                name: "ScheduleExceptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RemovalReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RemovedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RemovedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleExceptions", x => x.Id);
                    table.CheckConstraint("CK_ScheduleExceptions_Removal", "LEN([Reason])>0 AND (([IsActive]=1 AND [RemovedByUserId] IS NULL AND [RemovedAtUtc] IS NULL AND [RemovalReason] IS NULL) OR ([IsActive]=0 AND [RemovedByUserId] IS NOT NULL AND [RemovedAtUtc] IS NOT NULL AND [RemovalReason] IS NOT NULL AND LEN([RemovalReason])>0))");
                    table.ForeignKey(
                        name: "FK_ScheduleExceptions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleExceptions_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleExceptions_AspNetUsers_RemovedByUserId",
                        column: x => x.RemovedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleExceptions_DoctorPracticeLocations_LocationId_DoctorProfileId_UserId",
                        columns: x => new { x.LocationId, x.DoctorProfileId, x.UserId },
                        principalTable: "DoctorPracticeLocations",
                        principalColumns: new[] { "Id", "DoctorProfileId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleExceptions_DoctorProfiles_DoctorProfileId_UserId",
                        columns: x => new { x.DoctorProfileId, x.UserId },
                        principalTable: "DoctorProfiles",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    StartsLocal = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    EndsLocal = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    MaxCapacity = table.Column<int>(type: "int", nullable: false),
                    BookingCutoffMinutes = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleRules", x => x.Id);
                    table.UniqueConstraint("AK_ScheduleRules_Id_LocationId_DoctorProfileId_UserId", x => new { x.Id, x.LocationId, x.DoctorProfileId, x.UserId });
                    table.CheckConstraint("CK_ScheduleRules_Settings", "[Day] BETWEEN 0 AND 6 AND [StartsLocal]<[EndsLocal] AND DATEPART(second,[StartsLocal])=0 AND DATEPART(second,[EndsLocal])=0 AND [MaxCapacity]>0 AND [BookingCutoffMinutes]>=0 AND ([EffectiveUntil] IS NULL OR [EffectiveUntil]>=[EffectiveFrom])");
                    table.ForeignKey(
                        name: "FK_ScheduleRules_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleRules_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleRules_DoctorPracticeLocations_LocationId_DoctorProfileId_UserId",
                        columns: x => new { x.LocationId, x.DoctorProfileId, x.UserId },
                        principalTable: "DoctorPracticeLocations",
                        principalColumns: new[] { "Id", "DoctorProfileId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BookingSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceRuleVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StartsLocal = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    EndsLocal = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false, collation: "Latin1_General_100_BIN2"),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BookingClosesAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    MaxCapacity = table.Column<int>(type: "int", nullable: false),
                    AllocatedCount = table.Column<int>(type: "int", nullable: false),
                    BookingCutoffMinutes = table.Column<int>(type: "int", nullable: false),
                    BookingPaused = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CancelledByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingSessions", x => x.Id);
                    table.CheckConstraint("CK_BookingSessions_Capacity", "[MaxCapacity]>0 AND [AllocatedCount]>=0 AND [AllocatedCount]<=[MaxCapacity]");
                    table.CheckConstraint("CK_BookingSessions_Cutoff", "[BookingCutoffMinutes]>=0 AND DATEDIFF_BIG(second,[BookingClosesAtUtc],[EndsAtUtc])=CONVERT(bigint,[BookingCutoffMinutes])*60");
                    table.CheckConstraint("CK_BookingSessions_Source", "([SourceRuleId] IS NULL AND [SourceRuleVersion] IS NULL) OR ([SourceRuleId] IS NOT NULL AND [SourceRuleVersion] IS NOT NULL)");
                    table.CheckConstraint("CK_BookingSessions_State", "([Status]=1 AND [CancelledByUserId] IS NULL AND [CancelledAtUtc] IS NULL AND [CancellationReason] IS NULL) OR ([Status]=2 AND [CancelledByUserId] IS NOT NULL AND [CancelledAtUtc] IS NOT NULL AND [CancellationReason] IS NOT NULL AND LEN([CancellationReason])>0)");
                    table.CheckConstraint("CK_BookingSessions_Window", "[StartsAtUtc]<[EndsAtUtc] AND [StartsLocal]<[EndsLocal] AND DATEPART(second,[StartsLocal])=0 AND DATEPART(second,[EndsLocal])=0 AND [TimeZoneId]='Africa/Cairo'");
                    table.ForeignKey(
                        name: "FK_BookingSessions_AspNetUsers_CancelledByUserId",
                        column: x => x.CancelledByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingSessions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingSessions_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingSessions_DoctorPracticeLocations_LocationId_DoctorProfileId_UserId",
                        columns: x => new { x.LocationId, x.DoctorProfileId, x.UserId },
                        principalTable: "DoctorPracticeLocations",
                        principalColumns: new[] { "Id", "DoctorProfileId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingSessions_ScheduleRules_SourceRuleId_LocationId_DoctorProfileId_UserId",
                        columns: x => new { x.SourceRuleId, x.LocationId, x.DoctorProfileId, x.UserId },
                        principalTable: "ScheduleRules",
                        principalColumns: new[] { "Id", "LocationId", "DoctorProfileId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingSessions_CancelledByUserId",
                table: "BookingSessions",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSessions_CreatedByUserId",
                table: "BookingSessions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSessions_DoctorProfileId_Status_StartsAtUtc_EndsAtUtc",
                table: "BookingSessions",
                columns: new[] { "DoctorProfileId", "Status", "StartsAtUtc", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingSessions_LastModifiedByUserId",
                table: "BookingSessions",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSessions_LocationId_DoctorProfileId_UserId",
                table: "BookingSessions",
                columns: new[] { "LocationId", "DoctorProfileId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingSessions_LocationId_LocalDate_Id",
                table: "BookingSessions",
                columns: new[] { "LocationId", "LocalDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingSessions_SourceRuleId_LocalDate",
                table: "BookingSessions",
                columns: new[] { "SourceRuleId", "LocalDate" },
                unique: true,
                filter: "[SourceRuleId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BookingSessions_SourceRuleId_LocationId_DoctorProfileId_UserId",
                table: "BookingSessions",
                columns: new[] { "SourceRuleId", "LocationId", "DoctorProfileId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExceptions_CreatedByUserId",
                table: "ScheduleExceptions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExceptions_DoctorProfileId_LocalDate",
                table: "ScheduleExceptions",
                columns: new[] { "DoctorProfileId", "LocalDate" },
                unique: true,
                filter: "[LocationId] IS NULL AND [IsActive]=1");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExceptions_DoctorProfileId_UserId",
                table: "ScheduleExceptions",
                columns: new[] { "DoctorProfileId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExceptions_LastModifiedByUserId",
                table: "ScheduleExceptions",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExceptions_LocationId_DoctorProfileId_UserId",
                table: "ScheduleExceptions",
                columns: new[] { "LocationId", "DoctorProfileId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExceptions_LocationId_LocalDate",
                table: "ScheduleExceptions",
                columns: new[] { "LocationId", "LocalDate" },
                unique: true,
                filter: "[LocationId] IS NOT NULL AND [IsActive]=1");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExceptions_RemovedByUserId",
                table: "ScheduleExceptions",
                column: "RemovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleRules_CreatedByUserId",
                table: "ScheduleRules",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleRules_DoctorProfileId_IsActive_Day",
                table: "ScheduleRules",
                columns: new[] { "DoctorProfileId", "IsActive", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleRules_LastModifiedByUserId",
                table: "ScheduleRules",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleRules_LocationId",
                table: "ScheduleRules",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleRules_LocationId_DoctorProfileId_UserId",
                table: "ScheduleRules",
                columns: new[] { "LocationId", "DoctorProfileId", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [ScheduleRules]) OR EXISTS (SELECT 1 FROM [BookingSessions]) OR EXISTS (SELECT 1 FROM [ScheduleExceptions]) THROW 51005, 'Cannot remove scheduling or session history. Use a forward migration.', 1;");
            migrationBuilder.DropTable(
                name: "BookingSessions");

            migrationBuilder.DropTable(
                name: "ScheduleExceptions");

            migrationBuilder.DropTable(
                name: "ScheduleRules");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_DoctorPracticeLocations_Id_DoctorProfileId_UserId",
                table: "DoctorPracticeLocations");
        }
    }
}
