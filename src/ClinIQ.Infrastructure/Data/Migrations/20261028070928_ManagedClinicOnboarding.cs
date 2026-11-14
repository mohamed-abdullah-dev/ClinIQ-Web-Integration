using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagedClinicOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClinicBranches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClinicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    PublicPhone = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LicensingAuthority = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LicenseNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NormalizedLicensingAuthority = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    NormalizedLicenseNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicBranches", x => x.Id);
                    table.UniqueConstraint("AK_ClinicBranches_Id_ClinicId", x => new { x.Id, x.ClinicId });
                    table.ForeignKey(
                        name: "FK_ClinicBranches_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicBranches_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClinicEvidenceAccesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ViewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicEvidenceAccesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClinicEvidenceAccesses_AspNetUsers_ViewerUserId",
                        column: x => x.ViewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClinicManagementMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClinicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicManagementMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClinicManagementMemberships_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicManagementMemberships_AspNetUsers_GrantedByUserId",
                        column: x => x.GrantedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicManagementMemberships_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicManagementMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClinicRegistrationDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    MediaType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicRegistrationDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClinicRegistrationDocuments_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicRegistrationDocuments_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClinicRegistrationRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetClinicId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClinicName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    BranchName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    PublicPhone = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LicensingAuthority = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LicenseNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LicensedOperator = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ApplicantCapacity = table.Column<int>(type: "int", nullable: false),
                    AuthorityBasis = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    CurrentSubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedClinicId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedBranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicRegistrationRequests", x => x.Id);
                    table.UniqueConstraint("AK_ClinicRegistrationRequests_Id_ApplicantUserId", x => new { x.Id, x.ApplicantUserId });
                    table.CheckConstraint("CK_ClinicRegistrationRequests_Approval", "([Status]=5 AND [ApprovedClinicId] IS NOT NULL AND [ApprovedBranchId] IS NOT NULL AND ([TargetClinicId] IS NULL OR [TargetClinicId]=[ApprovedClinicId])) OR ([Status]<>5 AND [ApprovedClinicId] IS NULL AND [ApprovedBranchId] IS NULL)");
                    table.CheckConstraint("CK_ClinicRegistrationRequests_State", "([Status]=1 AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL) OR ([Status] IN (2,3,4,5) AND [AttemptNumber]>0 AND [CurrentSubmissionId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ClinicRegistrationRequests_AspNetUsers_ApplicantUserId",
                        column: x => x.ApplicantUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicRegistrationRequests_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicRegistrationRequests_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicRegistrationRequests_ClinicBranches_ApprovedBranchId_ApprovedClinicId",
                        columns: x => new { x.ApprovedBranchId, x.ApprovedClinicId },
                        principalTable: "ClinicBranches",
                        principalColumns: new[] { "Id", "ClinicId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClinicRegistrationSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetClinicId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    ClinicName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    BranchName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    PublicPhone = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LicensingAuthority = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LicenseNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LicensedOperator = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ApplicantCapacity = table.Column<int>(type: "int", nullable: false),
                    AuthorityBasis = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LicenseDetailsVerified = table.Column<bool>(type: "bit", nullable: false),
                    IdentityVerified = table.Column<bool>(type: "bit", nullable: false),
                    AuthorityVerified = table.Column<bool>(type: "bit", nullable: false),
                    IndependentlyCorroborated = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicRegistrationSubmissions", x => x.Id);
                    table.UniqueConstraint("AK_ClinicRegistrationSubmissions_Id_ApplicantUserId", x => new { x.Id, x.ApplicantUserId });
                    table.UniqueConstraint("AK_ClinicRegistrationSubmissions_Id_RequestId", x => new { x.Id, x.RequestId });
                    table.CheckConstraint("CK_ClinicRegistrationSubmissions_Decision", "([Status]=2 AND [ReviewerUserId] IS NULL AND [ReviewReason] IS NULL AND [ReviewedAtUtc] IS NULL AND [LicenseDetailsVerified]=0 AND [IdentityVerified]=0 AND [AuthorityVerified]=0 AND [IndependentlyCorroborated]=0) OR ([Status] IN (3,4,5) AND [ReviewerUserId] IS NOT NULL AND [ReviewerUserId]<>[ApplicantUserId] AND [ReviewReason] IS NOT NULL AND LEN([ReviewReason]) BETWEEN 1 AND 2000 AND [ReviewedAtUtc] IS NOT NULL AND ([Status]<>5 OR ([LicenseDetailsVerified]=1 AND [IdentityVerified]=1 AND [AuthorityVerified]=1 AND [IndependentlyCorroborated]=1)))");
                    table.ForeignKey(
                        name: "FK_ClinicRegistrationSubmissions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicRegistrationSubmissions_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicRegistrationSubmissions_AspNetUsers_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicRegistrationSubmissions_ClinicRegistrationRequests_RequestId_ApplicantUserId",
                        columns: x => new { x.RequestId, x.ApplicantUserId },
                        principalTable: "ClinicRegistrationRequests",
                        principalColumns: new[] { "Id", "ApplicantUserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Clinics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SourceSubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clinics", x => x.Id);
                    table.UniqueConstraint("AK_Clinics_Id_SourceSubmissionId", x => new { x.Id, x.SourceSubmissionId });
                    table.ForeignKey(
                        name: "FK_Clinics_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Clinics_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Clinics_ClinicRegistrationSubmissions_SourceSubmissionId",
                        column: x => x.SourceSubmissionId,
                        principalTable: "ClinicRegistrationSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicBranches_ClinicId",
                table: "ClinicBranches",
                column: "ClinicId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicBranches_CreatedByUserId",
                table: "ClinicBranches",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicBranches_LastModifiedByUserId",
                table: "ClinicBranches",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicBranches_NormalizedLicensingAuthority_NormalizedLicenseNumber",
                table: "ClinicBranches",
                columns: new[] { "NormalizedLicensingAuthority", "NormalizedLicenseNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClinicBranches_SourceSubmissionId",
                table: "ClinicBranches",
                column: "SourceSubmissionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClinicEvidenceAccesses_DocumentId_GrantedAtUtc",
                table: "ClinicEvidenceAccesses",
                columns: new[] { "DocumentId", "GrantedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicEvidenceAccesses_ViewerUserId",
                table: "ClinicEvidenceAccesses",
                column: "ViewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicManagementMemberships_ClinicId",
                table: "ClinicManagementMemberships",
                column: "ClinicId",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicManagementMemberships_ClinicId_SourceSubmissionId",
                table: "ClinicManagementMemberships",
                columns: new[] { "ClinicId", "SourceSubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicManagementMemberships_ClinicId_UserId",
                table: "ClinicManagementMemberships",
                columns: new[] { "ClinicId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClinicManagementMemberships_CreatedByUserId",
                table: "ClinicManagementMemberships",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicManagementMemberships_GrantedByUserId",
                table: "ClinicManagementMemberships",
                column: "GrantedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicManagementMemberships_LastModifiedByUserId",
                table: "ClinicManagementMemberships",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicManagementMemberships_SourceSubmissionId_UserId",
                table: "ClinicManagementMemberships",
                columns: new[] { "SourceSubmissionId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicManagementMemberships_UserId",
                table: "ClinicManagementMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationDocuments_CreatedByUserId",
                table: "ClinicRegistrationDocuments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationDocuments_LastModifiedByUserId",
                table: "ClinicRegistrationDocuments",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationDocuments_StorageKey",
                table: "ClinicRegistrationDocuments",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationDocuments_SubmissionId_Kind",
                table: "ClinicRegistrationDocuments",
                columns: new[] { "SubmissionId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationRequests_ApplicantUserId_CreatedAtUtc_Id",
                table: "ClinicRegistrationRequests",
                columns: new[] { "ApplicantUserId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationRequests_ApprovedBranchId_ApprovedClinicId",
                table: "ClinicRegistrationRequests",
                columns: new[] { "ApprovedBranchId", "ApprovedClinicId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationRequests_ApprovedClinicId",
                table: "ClinicRegistrationRequests",
                column: "ApprovedClinicId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationRequests_CreatedByUserId",
                table: "ClinicRegistrationRequests",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationRequests_CurrentSubmissionId_Id",
                table: "ClinicRegistrationRequests",
                columns: new[] { "CurrentSubmissionId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationRequests_LastModifiedByUserId",
                table: "ClinicRegistrationRequests",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationRequests_Status_CreatedAtUtc_Id",
                table: "ClinicRegistrationRequests",
                columns: new[] { "Status", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationRequests_TargetClinicId",
                table: "ClinicRegistrationRequests",
                column: "TargetClinicId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationSubmissions_CreatedByUserId",
                table: "ClinicRegistrationSubmissions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationSubmissions_LastModifiedByUserId",
                table: "ClinicRegistrationSubmissions",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationSubmissions_RequestId",
                table: "ClinicRegistrationSubmissions",
                column: "RequestId",
                unique: true,
                filter: "[Status] = 2");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationSubmissions_RequestId_ApplicantUserId",
                table: "ClinicRegistrationSubmissions",
                columns: new[] { "RequestId", "ApplicantUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationSubmissions_RequestId_AttemptNumber",
                table: "ClinicRegistrationSubmissions",
                columns: new[] { "RequestId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationSubmissions_ReviewerUserId",
                table: "ClinicRegistrationSubmissions",
                column: "ReviewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicRegistrationSubmissions_TargetClinicId",
                table: "ClinicRegistrationSubmissions",
                column: "TargetClinicId");

            migrationBuilder.CreateIndex(
                name: "IX_Clinics_CreatedByUserId",
                table: "Clinics",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Clinics_LastModifiedByUserId",
                table: "Clinics",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Clinics_SourceSubmissionId",
                table: "Clinics",
                column: "SourceSubmissionId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicBranches_ClinicRegistrationSubmissions_SourceSubmissionId",
                table: "ClinicBranches",
                column: "SourceSubmissionId",
                principalTable: "ClinicRegistrationSubmissions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicBranches_Clinics_ClinicId",
                table: "ClinicBranches",
                column: "ClinicId",
                principalTable: "Clinics",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicEvidenceAccesses_ClinicRegistrationDocuments_DocumentId",
                table: "ClinicEvidenceAccesses",
                column: "DocumentId",
                principalTable: "ClinicRegistrationDocuments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicManagementMemberships_ClinicRegistrationSubmissions_SourceSubmissionId_UserId",
                table: "ClinicManagementMemberships",
                columns: new[] { "SourceSubmissionId", "UserId" },
                principalTable: "ClinicRegistrationSubmissions",
                principalColumns: new[] { "Id", "ApplicantUserId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicManagementMemberships_Clinics_ClinicId_SourceSubmissionId",
                table: "ClinicManagementMemberships",
                columns: new[] { "ClinicId", "SourceSubmissionId" },
                principalTable: "Clinics",
                principalColumns: new[] { "Id", "SourceSubmissionId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicRegistrationDocuments_ClinicRegistrationSubmissions_SubmissionId",
                table: "ClinicRegistrationDocuments",
                column: "SubmissionId",
                principalTable: "ClinicRegistrationSubmissions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicRegistrationRequests_ClinicRegistrationSubmissions_CurrentSubmissionId_Id",
                table: "ClinicRegistrationRequests",
                columns: new[] { "CurrentSubmissionId", "Id" },
                principalTable: "ClinicRegistrationSubmissions",
                principalColumns: new[] { "Id", "RequestId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicRegistrationRequests_Clinics_ApprovedClinicId",
                table: "ClinicRegistrationRequests",
                column: "ApprovedClinicId",
                principalTable: "Clinics",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicRegistrationRequests_Clinics_TargetClinicId",
                table: "ClinicRegistrationRequests",
                column: "TargetClinicId",
                principalTable: "Clinics",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClinicRegistrationSubmissions_Clinics_TargetClinicId",
                table: "ClinicRegistrationSubmissions",
                column: "TargetClinicId",
                principalTable: "Clinics",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [ClinicRegistrationRequests]) OR EXISTS (SELECT 1 FROM [Clinics]) THROW 51002, 'Cannot remove clinic registration or management history. Use a forward migration.', 1;");
            migrationBuilder.DropForeignKey(
                name: "FK_ClinicBranches_ClinicRegistrationSubmissions_SourceSubmissionId",
                table: "ClinicBranches");

            migrationBuilder.DropForeignKey(
                name: "FK_ClinicRegistrationRequests_ClinicRegistrationSubmissions_CurrentSubmissionId_Id",
                table: "ClinicRegistrationRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_Clinics_ClinicRegistrationSubmissions_SourceSubmissionId",
                table: "Clinics");

            migrationBuilder.DropTable(
                name: "ClinicEvidenceAccesses");

            migrationBuilder.DropTable(
                name: "ClinicManagementMemberships");

            migrationBuilder.DropTable(
                name: "ClinicRegistrationDocuments");

            migrationBuilder.DropTable(
                name: "ClinicRegistrationSubmissions");

            migrationBuilder.DropTable(
                name: "ClinicRegistrationRequests");

            migrationBuilder.DropTable(
                name: "ClinicBranches");

            migrationBuilder.DropTable(
                name: "Clinics");
        }
    }
}
