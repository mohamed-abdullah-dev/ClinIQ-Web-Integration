using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PremisesCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PremisesCorrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApplicantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Original_Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Original_Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Original_Latitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    Original_Longitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    Original_PublicPhone = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Original_LicensingAuthority = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Original_LicenseNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Proposed_Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Proposed_Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Proposed_Latitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    Proposed_Longitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    Proposed_PublicPhone = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Proposed_LicensingAuthority = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Proposed_LicenseNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OriginalVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    RequestReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SamePremisesVerified = table.Column<bool>(type: "bit", nullable: false),
                    LicenseVerified = table.Column<bool>(type: "bit", nullable: false),
                    AuthorityVerified = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PremisesCorrections", x => x.Id);
                    table.CheckConstraint("CK_PremisesCorrections_Decision", "([Status]=1 AND [ReviewerUserId] IS NULL AND [ReviewReason] IS NULL AND [ReviewedAtUtc] IS NULL AND [SamePremisesVerified]=0 AND [LicenseVerified]=0 AND [AuthorityVerified]=0) OR ([Status] IN (2,3,4) AND [ReviewerUserId] IS NOT NULL AND [ReviewerUserId]<>[ApplicantUserId] AND LEN([ReviewReason]) BETWEEN 1 AND 2000 AND [ReviewReason] IS NOT NULL AND [ReviewedAtUtc] IS NOT NULL AND ([Status]<>2 OR ([SamePremisesVerified]=1 AND [LicenseVerified]=1 AND [AuthorityVerified]=1)))");
                    table.CheckConstraint("CK_PremisesCorrections_Original_Coordinates", "([Original_Latitude] IS NULL AND [Original_Longitude] IS NULL) OR ([Original_Latitude] IS NOT NULL AND [Original_Longitude] IS NOT NULL AND [Original_Latitude] BETWEEN -90 AND 90 AND [Original_Longitude] BETWEEN -180 AND 180)");
                    table.CheckConstraint("CK_PremisesCorrections_Original_Details", "LEN([Original_Name])>0 AND LEN([Original_Address])>0 AND (([BranchId] IS NULL AND [Original_PublicPhone] IS NULL AND [Original_LicensingAuthority] IS NULL AND [Original_LicenseNumber] IS NULL) OR ([BranchId] IS NOT NULL AND [Original_PublicPhone] IS NOT NULL AND LEN([Original_PublicPhone])>0 AND [Original_LicensingAuthority] IS NOT NULL AND LEN([Original_LicensingAuthority])>0 AND [Original_LicenseNumber] IS NOT NULL AND LEN([Original_LicenseNumber])>0))");
                    table.CheckConstraint("CK_PremisesCorrections_Proposed_Coordinates", "([Proposed_Latitude] IS NULL AND [Proposed_Longitude] IS NULL) OR ([Proposed_Latitude] IS NOT NULL AND [Proposed_Longitude] IS NOT NULL AND [Proposed_Latitude] BETWEEN -90 AND 90 AND [Proposed_Longitude] BETWEEN -180 AND 180)");
                    table.CheckConstraint("CK_PremisesCorrections_Proposed_Details", "LEN([Proposed_Name])>0 AND LEN([Proposed_Address])>0 AND (([BranchId] IS NULL AND [Proposed_PublicPhone] IS NULL AND [Proposed_LicensingAuthority] IS NULL AND [Proposed_LicenseNumber] IS NULL) OR ([BranchId] IS NOT NULL AND [Proposed_PublicPhone] IS NOT NULL AND LEN([Proposed_PublicPhone])>0 AND [Proposed_LicensingAuthority] IS NOT NULL AND LEN([Proposed_LicensingAuthority])>0 AND [Proposed_LicenseNumber] IS NOT NULL AND LEN([Proposed_LicenseNumber])>0))");
                    table.CheckConstraint("CK_PremisesCorrections_Reasons", "LEN([RequestReason]) BETWEEN 1 AND 2000");
                    table.CheckConstraint("CK_PremisesCorrections_Scope", "([LocationId] IS NOT NULL AND [BranchId] IS NULL) OR ([LocationId] IS NULL AND [BranchId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PremisesCorrections_AspNetUsers_ApplicantUserId",
                        column: x => x.ApplicantUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PremisesCorrections_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PremisesCorrections_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PremisesCorrections_AspNetUsers_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PremisesCorrections_ClinicBranches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "ClinicBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PremisesCorrections_DoctorPracticeLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "DoctorPracticeLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PremisesCorrectionDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_PremisesCorrectionDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PremisesCorrectionDocuments_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PremisesCorrectionDocuments_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PremisesCorrectionDocuments_PremisesCorrections_CorrectionId",
                        column: x => x.CorrectionId,
                        principalTable: "PremisesCorrections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PremisesCorrectionEvidenceAccesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ViewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PremisesCorrectionEvidenceAccesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PremisesCorrectionEvidenceAccesses_AspNetUsers_ViewerUserId",
                        column: x => x.ViewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PremisesCorrectionEvidenceAccesses_PremisesCorrectionDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "PremisesCorrectionDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrectionDocuments_CorrectionId_Kind",
                table: "PremisesCorrectionDocuments",
                columns: new[] { "CorrectionId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrectionDocuments_CreatedByUserId",
                table: "PremisesCorrectionDocuments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrectionDocuments_LastModifiedByUserId",
                table: "PremisesCorrectionDocuments",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrectionDocuments_StorageKey",
                table: "PremisesCorrectionDocuments",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrectionEvidenceAccesses_DocumentId_GrantedAtUtc",
                table: "PremisesCorrectionEvidenceAccesses",
                columns: new[] { "DocumentId", "GrantedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrectionEvidenceAccesses_ViewerUserId",
                table: "PremisesCorrectionEvidenceAccesses",
                column: "ViewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrections_ApplicantUserId_CreatedAtUtc_Id",
                table: "PremisesCorrections",
                columns: new[] { "ApplicantUserId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrections_BranchId",
                table: "PremisesCorrections",
                column: "BranchId",
                unique: true,
                filter: "[Status]=1 AND [BranchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrections_CreatedByUserId",
                table: "PremisesCorrections",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrections_LastModifiedByUserId",
                table: "PremisesCorrections",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrections_LocationId",
                table: "PremisesCorrections",
                column: "LocationId",
                unique: true,
                filter: "[Status]=1 AND [LocationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrections_ReviewerUserId",
                table: "PremisesCorrections",
                column: "ReviewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PremisesCorrections_Status_CreatedAtUtc_Id",
                table: "PremisesCorrections",
                columns: new[] { "Status", "CreatedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [PremisesCorrections]) THROW 51003, 'Cannot remove premises correction history. Use a forward migration.', 1;");
            migrationBuilder.DropTable(
                name: "PremisesCorrectionEvidenceAccesses");

            migrationBuilder.DropTable(
                name: "PremisesCorrectionDocuments");

            migrationBuilder.DropTable(
                name: "PremisesCorrections");
        }
    }
}
