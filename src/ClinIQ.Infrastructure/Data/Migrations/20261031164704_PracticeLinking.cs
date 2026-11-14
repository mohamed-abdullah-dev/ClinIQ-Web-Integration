using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PracticeLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF;");
            migrationBuilder.DropCheckConstraint(
                name: "CK_DoctorPracticeLocations_State",
                table: "DoctorPracticeLocations");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceLinkId",
                table: "DoctorPracticeLocations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PracticeLinkRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Terms = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    LocationVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    BranchVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    LocationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LocationAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    BranchName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BranchAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ConsultationFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PracticeLinkRequests", x => x.Id);
                    table.UniqueConstraint("AK_PracticeLinkRequests_Id_LocationId_BranchId", x => new { x.Id, x.LocationId, x.BranchId });
                    table.CheckConstraint("CK_PracticeLinkRequests_Decision", "([Status]=1 AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [DecisionReason] IS NULL) OR ([Status] IN (2,3,4) AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc]>=[RequestedAtUtc] AND [DecidedAtUtc] IS NOT NULL AND [DecisionReason] IS NOT NULL AND LEN([DecisionReason]) BETWEEN 1 AND 2000 AND ([Status]<>4 OR [DecidedByUserId]=[DoctorUserId]))");
                    table.CheckConstraint("CK_PracticeLinkRequests_Terms", "[Terms]='SamePremises-ManagedFee-NewSessionsOnly-v1' AND [ConsultationFee]>=0 AND [Currency]='EGP'");
                    table.ForeignKey(
                        name: "FK_PracticeLinkRequests_AspNetUsers_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeLinkRequests_AspNetUsers_DoctorUserId",
                        column: x => x.DoctorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeLinkRequests_ClinicBranches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "ClinicBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeLinkRequests_DoctorPracticeLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "DoctorPracticeLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorPracticeLocations_SourceLinkId_Id_ClinicBranchId",
                table: "DoctorPracticeLocations",
                columns: new[] { "SourceLinkId", "Id", "ClinicBranchId" });

            migrationBuilder.Sql("EXEC(N'ALTER TABLE [DoctorPracticeLocations] ADD CONSTRAINT [CK_DoctorPracticeLocations_State] CHECK (([ClinicBranchId] IS NULL AND [SourceInvitationId] IS NULL AND [SourceLinkId] IS NULL AND (([Status]=1 AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL) OR ([Status] IN (2,3,4,5) AND [AttemptNumber]>0 AND [CurrentSubmissionId] IS NOT NULL))) OR ([ClinicBranchId] IS NOT NULL AND [Status]=5 AND (([SourceInvitationId] IS NOT NULL AND [SourceLinkId] IS NULL AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL) OR ([SourceInvitationId] IS NULL AND [SourceLinkId] IS NOT NULL AND [AttemptNumber]>0 AND [CurrentSubmissionId] IS NOT NULL))))');");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLinkRequests_BranchId_Status_RequestedAtUtc_Id",
                table: "PracticeLinkRequests",
                columns: new[] { "BranchId", "Status", "RequestedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLinkRequests_DecidedByUserId",
                table: "PracticeLinkRequests",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLinkRequests_DoctorUserId_RequestedAtUtc_Id",
                table: "PracticeLinkRequests",
                columns: new[] { "DoctorUserId", "RequestedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLinkRequests_LocationId",
                table: "PracticeLinkRequests",
                column: "LocationId",
                unique: true,
                filter: "[Status]=1");

            migrationBuilder.AddForeignKey(
                name: "FK_DoctorPracticeLocations_PracticeLinkRequests_SourceLinkId_Id_ClinicBranchId",
                table: "DoctorPracticeLocations",
                columns: new[] { "SourceLinkId", "Id", "ClinicBranchId" },
                principalTable: "PracticeLinkRequests",
                principalColumns: new[] { "Id", "LocationId", "BranchId" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER dbo.TR_PracticeLinkRequests_Protect ON dbo.PracticeLinkRequests AFTER INSERT, UPDATE, DELETE AS
                BEGIN
                  SET NOCOUNT ON;
                  IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.Id=d.Id WHERE i.Id IS NULL OR d.Status<>1)
                    THROW 51022, ''Link history and final consent cannot be deleted or rewritten.'', 1;
                  IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id=i.Id WHERE d.Id IS NULL AND i.Status<>1)
                    THROW 51022, ''A link request starts pending.'', 1;
                  IF EXISTS(
                    SELECT Id,LocationId,BranchId,DoctorUserId,CONVERT(varbinary(max),Terms),LocationVersion,BranchVersion,
                      CONVERT(varbinary(max),LocationName),CONVERT(varbinary(max),LocationAddress),CONVERT(varbinary(max),BranchName),CONVERT(varbinary(max),BranchAddress),ConsultationFee,CONVERT(varbinary(max),Currency),RequestedAtUtc FROM deleted
                    EXCEPT
                    SELECT Id,LocationId,BranchId,DoctorUserId,CONVERT(varbinary(max),Terms),LocationVersion,BranchVersion,
                      CONVERT(varbinary(max),LocationName),CONVERT(varbinary(max),LocationAddress),CONVERT(varbinary(max),BranchName),CONVERT(varbinary(max),BranchAddress),ConsultationFee,CONVERT(varbinary(max),Currency),RequestedAtUtc FROM inserted)
                    THROW 51022, ''The agreed source snapshots are immutable.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS(SELECT 1 FROM PracticeLinkRequests) OR EXISTS(SELECT 1 FROM DoctorPracticeLocations WHERE SourceLinkId IS NOT NULL) THROW 51023, 'Cannot remove link consent or linked provenance.', 1;");
            migrationBuilder.DropForeignKey(
                name: "FK_DoctorPracticeLocations_PracticeLinkRequests_SourceLinkId_Id_ClinicBranchId",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropTable(
                name: "PracticeLinkRequests");

            migrationBuilder.DropIndex(
                name: "IX_DoctorPracticeLocations_SourceLinkId_Id_ClinicBranchId",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DoctorPracticeLocations_State",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropColumn(
                name: "SourceLinkId",
                table: "DoctorPracticeLocations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DoctorPracticeLocations_State",
                table: "DoctorPracticeLocations",
                sql: "([ClinicBranchId] IS NULL AND [SourceInvitationId] IS NULL AND (([Status]=1 AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL) OR ([Status] IN (2,3,4,5) AND [AttemptNumber]>0 AND [CurrentSubmissionId] IS NOT NULL))) OR ([ClinicBranchId] IS NOT NULL AND [SourceInvitationId] IS NOT NULL AND [Status]=5 AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL)");
        }
    }
}
