using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DoctorPracticeLocations_State",
                table: "DoctorPracticeLocations");

            migrationBuilder.AddColumn<Guid>(
                name: "ClinicBranchId",
                table: "DoctorPracticeLocations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "DoctorPracticeLocations",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceInvitationId",
                table: "DoctorPracticeLocations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PracticeFeeChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NewFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DeliveryStatus = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    LastDeliveryAttemptUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PracticeFeeChanges", x => x.Id);
                    table.CheckConstraint("CK_PracticeFeeChanges_Delivery", "[DeliveryStatus] IN('Pending','DevelopmentFile','Unavailable')");
                    table.CheckConstraint("CK_PracticeFeeChanges_Price", "[PreviousFee]>=0 AND [NewFee]>=0 AND [PreviousFee]<>[NewFee] AND [Currency]='EGP'");
                    table.ForeignKey(
                        name: "FK_PracticeFeeChanges_AspNetUsers_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeFeeChanges_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeFeeChanges_AspNetUsers_DoctorUserId",
                        column: x => x.DoctorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeFeeChanges_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeFeeChanges_DoctorPracticeLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "DoctorPracticeLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IssuerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SecretHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Terms = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ConsultationFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReplacesInvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryStatus = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkInvitations", x => x.Id);
                    table.CheckConstraint("CK_WorkInvitations_Delivery", "[DeliveryStatus] IN('Pending','DevelopmentFile','Unavailable')");
                    table.CheckConstraint("CK_WorkInvitations_Fee", "([Kind]=1 AND [ConsultationFee] IS NOT NULL AND [ConsultationFee]>=0 AND [Currency] IS NOT NULL AND [Currency]='EGP') OR ([Kind]=2 AND [ConsultationFee] IS NULL AND [Currency] IS NULL)");
                    table.CheckConstraint("CK_WorkInvitations_Scope", "([Kind]=1 AND [BranchId] IS NOT NULL AND [LocationId] IS NULL) OR ([Kind]=2 AND (([BranchId] IS NOT NULL AND [LocationId] IS NULL) OR ([BranchId] IS NULL AND [LocationId] IS NOT NULL)))");
                    table.CheckConstraint("CK_WorkInvitations_Status", "([Status]=1 AND [ClosedAtUtc] IS NULL AND [ClosedByUserId] IS NULL AND [ClosureReason] IS NULL) OR ([Status] IN(2,3) AND [ClosedAtUtc] IS NOT NULL AND [ClosedByUserId] IS NOT NULL AND [ClosureReason] IS NULL) OR ([Status] IN(4,5,6) AND [ClosedAtUtc] IS NOT NULL AND [ClosedByUserId] IS NOT NULL AND [ClosureReason] IS NOT NULL AND LEN([ClosureReason])>0)");
                    table.CheckConstraint("CK_WorkInvitations_Terms", "LEN([Terms])>0 AND [ExpiresAtUtc]>[CreatedAtUtc]");
                    table.ForeignKey(
                        name: "FK_WorkInvitations_AspNetUsers_ClosedByUserId",
                        column: x => x.ClosedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkInvitations_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkInvitations_AspNetUsers_IssuerUserId",
                        column: x => x.IssuerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkInvitations_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkInvitations_AspNetUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkInvitations_ClinicBranches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "ClinicBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkInvitations_DoctorPracticeLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "DoctorPracticeLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkInvitations_WorkInvitations_ReplacesInvitationId",
                        column: x => x.ReplacesInvitationId,
                        principalTable: "WorkInvitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceInvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RevokedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevocationReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistAssignments", x => x.Id);
                    table.CheckConstraint("CK_ReceptionistAssignments_Revocation", "([IsActive]=1 AND [RevokedByUserId] IS NULL AND [RevokedAtUtc] IS NULL AND [RevocationReason] IS NULL) OR ([IsActive]=0 AND [RevokedByUserId] IS NOT NULL AND [RevokedAtUtc] IS NOT NULL AND [RevocationReason] IS NOT NULL AND LEN([RevocationReason])>0)");
                    table.CheckConstraint("CK_ReceptionistAssignments_Scope", "([BranchId] IS NOT NULL AND [LocationId] IS NULL) OR ([BranchId] IS NULL AND [LocationId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ReceptionistAssignments_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistAssignments_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistAssignments_AspNetUsers_RevokedByUserId",
                        column: x => x.RevokedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistAssignments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistAssignments_ClinicBranches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "ClinicBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistAssignments_DoctorPracticeLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "DoctorPracticeLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistAssignments_WorkInvitations_SourceInvitationId",
                        column: x => x.SourceInvitationId,
                        principalTable: "WorkInvitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorPracticeLocations_ClinicBranchId",
                table: "DoctorPracticeLocations",
                column: "ClinicBranchId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorPracticeLocations_DoctorProfileId_ClinicBranchId",
                table: "DoctorPracticeLocations",
                columns: new[] { "DoctorProfileId", "ClinicBranchId" },
                unique: true,
                filter: "[ClinicBranchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorPracticeLocations_SourceInvitationId",
                table: "DoctorPracticeLocations",
                column: "SourceInvitationId",
                unique: true,
                filter: "[SourceInvitationId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DoctorPracticeLocations_State",
                table: "DoctorPracticeLocations",
                sql: "([ClinicBranchId] IS NULL AND [SourceInvitationId] IS NULL AND (([Status]=1 AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL) OR ([Status] IN (2,3,4,5) AND [AttemptNumber]>0 AND [CurrentSubmissionId] IS NOT NULL))) OR ([ClinicBranchId] IS NOT NULL AND [SourceInvitationId] IS NOT NULL AND [Status]=5 AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeFeeChanges_ChangedByUserId",
                table: "PracticeFeeChanges",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeFeeChanges_CreatedByUserId",
                table: "PracticeFeeChanges",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeFeeChanges_DoctorUserId",
                table: "PracticeFeeChanges",
                column: "DoctorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeFeeChanges_LastModifiedByUserId",
                table: "PracticeFeeChanges",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeFeeChanges_LocationId_CreatedAtUtc_Id",
                table: "PracticeFeeChanges",
                columns: new[] { "LocationId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAssignments_BranchId",
                table: "ReceptionistAssignments",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAssignments_CreatedByUserId",
                table: "ReceptionistAssignments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAssignments_LastModifiedByUserId",
                table: "ReceptionistAssignments",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAssignments_LocationId",
                table: "ReceptionistAssignments",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAssignments_RevokedByUserId",
                table: "ReceptionistAssignments",
                column: "RevokedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAssignments_SourceInvitationId",
                table: "ReceptionistAssignments",
                column: "SourceInvitationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAssignments_UserId_BranchId",
                table: "ReceptionistAssignments",
                columns: new[] { "UserId", "BranchId" },
                unique: true,
                filter: "[IsActive]=1 AND [BranchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAssignments_UserId_LocationId",
                table: "ReceptionistAssignments",
                columns: new[] { "UserId", "LocationId" },
                unique: true,
                filter: "[IsActive]=1 AND [LocationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkInvitations_BranchId_Kind_NormalizedEmail",
                table: "WorkInvitations",
                columns: new[] { "BranchId", "Kind", "NormalizedEmail" },
                unique: true,
                filter: "[Status]=1 AND [BranchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkInvitations_ClosedByUserId",
                table: "WorkInvitations",
                column: "ClosedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkInvitations_CreatedByUserId",
                table: "WorkInvitations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkInvitations_IssuerUserId",
                table: "WorkInvitations",
                column: "IssuerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkInvitations_LastModifiedByUserId",
                table: "WorkInvitations",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkInvitations_LocationId_Kind_NormalizedEmail",
                table: "WorkInvitations",
                columns: new[] { "LocationId", "Kind", "NormalizedEmail" },
                unique: true,
                filter: "[Status]=1 AND [LocationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkInvitations_NormalizedEmail_CreatedAtUtc_Id",
                table: "WorkInvitations",
                columns: new[] { "NormalizedEmail", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkInvitations_ReplacesInvitationId",
                table: "WorkInvitations",
                column: "ReplacesInvitationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkInvitations_TargetUserId",
                table: "WorkInvitations",
                column: "TargetUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_DoctorPracticeLocations_ClinicBranches_ClinicBranchId",
                table: "DoctorPracticeLocations",
                column: "ClinicBranchId",
                principalTable: "ClinicBranches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DoctorPracticeLocations_WorkInvitations_SourceInvitationId",
                table: "DoctorPracticeLocations",
                column: "SourceInvitationId",
                principalTable: "WorkInvitations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [WorkInvitations]) OR EXISTS (SELECT 1 FROM [ReceptionistAssignments]) OR EXISTS (SELECT 1 FROM [PracticeFeeChanges]) OR EXISTS (SELECT 1 FROM [DoctorPracticeLocations] WHERE [ClinicBranchId] IS NOT NULL OR [IsActive]=0) THROW 51004, 'Cannot remove workforce or invitation history. Use a forward migration.', 1;");
            migrationBuilder.DropForeignKey(
                name: "FK_DoctorPracticeLocations_ClinicBranches_ClinicBranchId",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropForeignKey(
                name: "FK_DoctorPracticeLocations_WorkInvitations_SourceInvitationId",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropTable(
                name: "PracticeFeeChanges");

            migrationBuilder.DropTable(
                name: "ReceptionistAssignments");

            migrationBuilder.DropTable(
                name: "WorkInvitations");

            migrationBuilder.DropIndex(
                name: "IX_DoctorPracticeLocations_ClinicBranchId",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropIndex(
                name: "IX_DoctorPracticeLocations_DoctorProfileId_ClinicBranchId",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropIndex(
                name: "IX_DoctorPracticeLocations_SourceInvitationId",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DoctorPracticeLocations_State",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropColumn(
                name: "ClinicBranchId",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropColumn(
                name: "SourceInvitationId",
                table: "DoctorPracticeLocations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DoctorPracticeLocations_State",
                table: "DoctorPracticeLocations",
                sql: "([Status]=1 AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL) OR ([Status] IN (2,3,4,5) AND [AttemptNumber]>0 AND [CurrentSubmissionId] IS NOT NULL)");
        }
    }
}
