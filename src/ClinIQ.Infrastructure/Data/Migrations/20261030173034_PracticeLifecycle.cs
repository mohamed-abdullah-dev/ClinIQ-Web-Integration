using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PracticeLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF;");
            migrationBuilder.AddColumn<int>(
                name: "LifecycleState",
                table: "DoctorPracticeLocations",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "LifecycleState",
                table: "ClinicBranches",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "OperationalEmergencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequesterUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewedPreviewHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    ReviewedScopeVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    CancelledCount = table.Column<int>(type: "int", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalEmergencies", x => x.Id);
                    table.CheckConstraint("CK_OperationalEmergencies_Facts", "(([LocationId] IS NOT NULL AND [BranchId] IS NULL) OR ([LocationId] IS NULL AND [BranchId] IS NOT NULL)) AND [Category] IN(1,2) AND LEN([Reason])>0 AND (([Status]=1 AND [ReviewerUserId] IS NULL AND [ReviewReason] IS NULL AND [ReviewedAtUtc] IS NULL AND [ReviewedPreviewHash] IS NULL AND [ReviewedScopeVersion] IS NULL AND [CancelledCount] IS NULL) OR ([Status] IN(2,3) AND [ReviewerUserId] IS NOT NULL AND [ReviewerUserId]<>[RequesterUserId] AND [ReviewReason] IS NOT NULL AND LEN([ReviewReason])>0 AND [ReviewedAtUtc] IS NOT NULL AND [ReviewedPreviewHash] IS NOT NULL AND [ReviewedScopeVersion] IS NOT NULL AND [CancelledCount] IS NOT NULL AND [CancelledCount]>=0 AND ([Status]=2 OR [CancelledCount]=0)))");
                    table.ForeignKey(
                        name: "FK_OperationalEmergencies_AspNetUsers_RequesterUserId",
                        column: x => x.RequesterUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationalEmergencies_AspNetUsers_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationalEmergencies_ClinicBranches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "ClinicBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationalEmergencies_DoctorPracticeLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "DoctorPracticeLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationalScopeRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BeforeState = table.Column<int>(type: "int", nullable: false),
                    AfterState = table.Column<int>(type: "int", nullable: false),
                    SourceVersion = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalScopeRecords", x => x.Id);
                    table.CheckConstraint("CK_OperationalScopeRecords_Facts", "(([LocationId] IS NOT NULL AND [BranchId] IS NULL) OR ([LocationId] IS NULL AND [BranchId] IS NOT NULL)) AND LEN([Reason])>0 AND (([Action]='Begin' AND [BeforeState]=1 AND [AfterState]=2) OR ([Action]='Finalize' AND [BeforeState]=2 AND [AfterState]=3))");
                    table.ForeignKey(
                        name: "FK_OperationalScopeRecords_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationalScopeRecords_ClinicBranches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "ClinicBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationalScopeRecords_DoctorPracticeLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "DoctorPracticeLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmergencyCancellationRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", maxLength: 32000, nullable: false),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", maxLength: 32000, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmergencyCancellationRecords", x => x.Id);
                    table.CheckConstraint("CK_EmergencyCancellationRecords_Json", "ISJSON([BeforeJson])=1 AND ISJSON([AfterJson])=1");
                    table.ForeignKey(
                        name: "FK_EmergencyCancellationRecords_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmergencyCancellationRecords_OperationalEmergencies_RequestId",
                        column: x => x.RequestId,
                        principalTable: "OperationalEmergencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("EXEC(N'ALTER TABLE [DoctorPracticeLocations] ADD CONSTRAINT [CK_DoctorPracticeLocations_Lifecycle] CHECK ([LifecycleState] IN (1,2,3) AND ([LifecycleState]<>3 OR [IsActive]=0));');");

            migrationBuilder.Sql("EXEC(N'ALTER TABLE [ClinicBranches] ADD CONSTRAINT [CK_ClinicBranches_Lifecycle] CHECK ([LifecycleState] IN (1,2,3) AND ([LifecycleState]<>3 OR [IsActive]=0));');");

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyCancellationRecords_AppointmentId",
                table: "EmergencyCancellationRecords",
                column: "AppointmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyCancellationRecords_RequestId",
                table: "EmergencyCancellationRecords",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalEmergencies_BranchId",
                table: "OperationalEmergencies",
                column: "BranchId",
                unique: true,
                filter: "[BranchId] IS NOT NULL AND [Status]=1");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalEmergencies_LocationId",
                table: "OperationalEmergencies",
                column: "LocationId",
                unique: true,
                filter: "[LocationId] IS NOT NULL AND [Status]=1");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalEmergencies_RequesterUserId",
                table: "OperationalEmergencies",
                column: "RequesterUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalEmergencies_ReviewerUserId",
                table: "OperationalEmergencies",
                column: "ReviewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalEmergencies_Status_CreatedAtUtc_Id",
                table: "OperationalEmergencies",
                columns: new[] { "Status", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalScopeRecords_ActorUserId",
                table: "OperationalScopeRecords",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalScopeRecords_BranchId_Action",
                table: "OperationalScopeRecords",
                columns: new[] { "BranchId", "Action" },
                unique: true,
                filter: "[BranchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalScopeRecords_LocationId_Action",
                table: "OperationalScopeRecords",
                columns: new[] { "LocationId", "Action" },
                unique: true,
                filter: "[LocationId] IS NOT NULL");

            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER TR_OperationalScopeRecords_Immutable ON OperationalScopeRecords AFTER UPDATE, DELETE AS
                BEGIN SET NOCOUNT ON; THROW 51018, ''Operational history is immutable.'', 1; END;');
                EXEC(N'CREATE TRIGGER TR_EmergencyCancellationRecords_Immutable ON EmergencyCancellationRecords AFTER UPDATE, DELETE AS
                BEGIN SET NOCOUNT ON; THROW 51020, ''Emergency cancellation history is immutable.'', 1; END;');
                EXEC(N'CREATE TRIGGER TR_OperationalEmergencies_Protect ON OperationalEmergencies AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.Id=d.Id WHERE i.Id IS NULL OR d.Status<>1 OR
                        EXISTS(SELECT i.LocationId,i.BranchId,i.RequesterUserId,i.Category,i.Reason,i.CreatedAtUtc
                            EXCEPT SELECT d.LocationId,d.BranchId,d.RequesterUserId,d.Category,d.Reason,d.CreatedAtUtc))
                        THROW 51019, ''Emergency origin and final decisions are immutable.'', 1;
                END;');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS(SELECT 1 FROM OperationalScopeRecords) OR EXISTS(SELECT 1 FROM OperationalEmergencies)
                    OR EXISTS(SELECT 1 FROM EmergencyCancellationRecords)
                    OR EXISTS(SELECT 1 FROM DoctorPracticeLocations WHERE LifecycleState<>1)
                    OR EXISTS(SELECT 1 FROM ClinicBranches WHERE LifecycleState<>1)
                    THROW 51021, 'Operational lifecycle rollback requires an explicit reviewed preservation plan.', 1;
                """);
            migrationBuilder.DropTable(
                name: "EmergencyCancellationRecords");

            migrationBuilder.DropTable(
                name: "OperationalScopeRecords");

            migrationBuilder.DropTable(
                name: "OperationalEmergencies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DoctorPracticeLocations_Lifecycle",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ClinicBranches_Lifecycle",
                table: "ClinicBranches");

            migrationBuilder.DropColumn(
                name: "LifecycleState",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropColumn(
                name: "LifecycleState",
                table: "ClinicBranches");
        }
    }
}
