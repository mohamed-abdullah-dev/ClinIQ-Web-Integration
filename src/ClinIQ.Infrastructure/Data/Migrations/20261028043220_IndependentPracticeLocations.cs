using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class IndependentPracticeLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DoctorPracticeLocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    ConsultationFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    CurrentSubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorPracticeLocations", x => x.Id);
                    table.CheckConstraint("CK_DoctorPracticeLocations_Coordinates", "([Latitude] IS NULL AND [Longitude] IS NULL) OR ([Latitude] IS NOT NULL AND [Longitude] IS NOT NULL AND [Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180)");
                    table.CheckConstraint("CK_DoctorPracticeLocations_Price", "[ConsultationFee]>=0 AND [Currency]='EGP'");
                    table.CheckConstraint("CK_DoctorPracticeLocations_State", "([Status]=1 AND [AttemptNumber]=0 AND [CurrentSubmissionId] IS NULL) OR ([Status] IN (2,3,4,5) AND [AttemptNumber]>0 AND [CurrentSubmissionId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_DoctorPracticeLocations_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorPracticeLocations_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorPracticeLocations_DoctorProfiles_DoctorProfileId_UserId",
                        columns: x => new { x.DoctorProfileId, x.UserId },
                        principalTable: "DoctorProfiles",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PracticeLocationSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(12,9)", precision: 12, scale: 9, nullable: true),
                    ConsultationFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LicenseMatchesAddress = table.Column<bool>(type: "bit", nullable: false),
                    DoctorWorkLinkVerified = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PracticeLocationSubmissions", x => x.Id);
                    table.UniqueConstraint("AK_PracticeLocationSubmissions_Id_LocationId", x => new { x.Id, x.LocationId });
                    table.CheckConstraint("CK_PracticeLocationSubmissions_Attempt", "[AttemptNumber]>0");
                    table.CheckConstraint("CK_PracticeLocationSubmissions_Decision", "([Status]=2 AND [ReviewerUserId] IS NULL AND [ReviewReason] IS NULL AND [ReviewedAtUtc] IS NULL AND [LicenseMatchesAddress]=0 AND [DoctorWorkLinkVerified]=0) OR ([Status] IN (3,4,5) AND [ReviewerUserId] IS NOT NULL AND [ReviewReason] IS NOT NULL AND LEN([ReviewReason]) BETWEEN 1 AND 2000 AND [ReviewedAtUtc] IS NOT NULL AND ([Status]<>5 OR ([LicenseMatchesAddress]=1 AND [DoctorWorkLinkVerified]=1)))");
                    table.CheckConstraint("CK_PracticeLocationSubmissions_Price", "[ConsultationFee]>=0 AND [Currency]='EGP'");
                    table.ForeignKey(
                        name: "FK_PracticeLocationSubmissions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeLocationSubmissions_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeLocationSubmissions_AspNetUsers_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeLocationSubmissions_DoctorPracticeLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "DoctorPracticeLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PracticeLocationDocuments",
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
                    table.PrimaryKey("PK_PracticeLocationDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PracticeLocationDocuments_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeLocationDocuments_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeLocationDocuments_PracticeLocationSubmissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "PracticeLocationSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PracticeLocationEvidenceAccesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ViewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PracticeLocationEvidenceAccesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PracticeLocationEvidenceAccesses_AspNetUsers_ViewerUserId",
                        column: x => x.ViewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PracticeLocationEvidenceAccesses_PracticeLocationDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "PracticeLocationDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorPracticeLocations_CreatedByUserId",
                table: "DoctorPracticeLocations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorPracticeLocations_CurrentSubmissionId_Id",
                table: "DoctorPracticeLocations",
                columns: new[] { "CurrentSubmissionId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorPracticeLocations_DoctorProfileId_UserId",
                table: "DoctorPracticeLocations",
                columns: new[] { "DoctorProfileId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorPracticeLocations_LastModifiedByUserId",
                table: "DoctorPracticeLocations",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorPracticeLocations_Status_CreatedAtUtc_Id",
                table: "DoctorPracticeLocations",
                columns: new[] { "Status", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorPracticeLocations_UserId_CreatedAtUtc_Id",
                table: "DoctorPracticeLocations",
                columns: new[] { "UserId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLocationDocuments_CreatedByUserId",
                table: "PracticeLocationDocuments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLocationDocuments_LastModifiedByUserId",
                table: "PracticeLocationDocuments",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLocationDocuments_StorageKey",
                table: "PracticeLocationDocuments",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLocationDocuments_SubmissionId_Kind",
                table: "PracticeLocationDocuments",
                columns: new[] { "SubmissionId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLocationEvidenceAccesses_DocumentId_GrantedAtUtc",
                table: "PracticeLocationEvidenceAccesses",
                columns: new[] { "DocumentId", "GrantedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLocationEvidenceAccesses_ViewerUserId",
                table: "PracticeLocationEvidenceAccesses",
                column: "ViewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLocationSubmissions_CreatedByUserId",
                table: "PracticeLocationSubmissions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLocationSubmissions_LastModifiedByUserId",
                table: "PracticeLocationSubmissions",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLocationSubmissions_LocationId",
                table: "PracticeLocationSubmissions",
                column: "LocationId",
                unique: true,
                filter: "[Status] = 2");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLocationSubmissions_LocationId_AttemptNumber",
                table: "PracticeLocationSubmissions",
                columns: new[] { "LocationId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLocationSubmissions_ReviewerUserId",
                table: "PracticeLocationSubmissions",
                column: "ReviewerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_DoctorPracticeLocations_PracticeLocationSubmissions_CurrentSubmissionId_Id",
                table: "DoctorPracticeLocations",
                columns: new[] { "CurrentSubmissionId", "Id" },
                principalTable: "PracticeLocationSubmissions",
                principalColumns: new[] { "Id", "LocationId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [DoctorPracticeLocations]) THROW 51001, 'Cannot remove practice-location data. Use a forward migration.', 1;");
            migrationBuilder.DropForeignKey(
                name: "FK_DoctorPracticeLocations_PracticeLocationSubmissions_CurrentSubmissionId_Id",
                table: "DoctorPracticeLocations");

            migrationBuilder.DropTable(
                name: "PracticeLocationEvidenceAccesses");

            migrationBuilder.DropTable(
                name: "PracticeLocationDocuments");

            migrationBuilder.DropTable(
                name: "PracticeLocationSubmissions");

            migrationBuilder.DropTable(
                name: "DoctorPracticeLocations");
        }
    }
}
