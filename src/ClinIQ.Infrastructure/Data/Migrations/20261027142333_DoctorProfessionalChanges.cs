using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DoctorProfessionalChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                table: "DoctorProfiles",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_DoctorProfiles_Id_UserId",
                table: "DoctorProfiles",
                columns: new[] { "Id", "UserId" });

            migrationBuilder.CreateTable(
                name: "DoctorProfessionalChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalProfessionalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OriginalSpecialtyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalProfessionalGrade = table.Column<int>(type: "int", nullable: false),
                    OriginalProfileVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    ProfessionalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SpecialtyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfessionalGrade = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorProfessionalChanges", x => x.Id);
                    table.CheckConstraint("CK_DoctorProfessionalChanges_Decision", "([Status] = 1 AND [ReviewerUserId] IS NULL AND [ReviewedAtUtc] IS NULL AND [ReviewReason] IS NULL) OR ([Status] IN (2,3) AND [ReviewerUserId] IS NOT NULL AND [ReviewerUserId] <> [UserId] AND [ReviewedAtUtc] IS NOT NULL AND [ReviewReason] IS NOT NULL AND LEN([ReviewReason]) BETWEEN 1 AND 2000)");
                    table.ForeignKey(
                        name: "FK_DoctorProfessionalChanges_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorProfessionalChanges_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorProfessionalChanges_AspNetUsers_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorProfessionalChanges_DoctorProfiles_DoctorProfileId_UserId",
                        columns: x => new { x.DoctorProfileId, x.UserId },
                        principalTable: "DoctorProfiles",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorProfessionalChanges_Specialties_OriginalSpecialtyId",
                        column: x => x.OriginalSpecialtyId,
                        principalTable: "Specialties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorProfessionalChanges_Specialties_SpecialtyId",
                        column: x => x.SpecialtyId,
                        principalTable: "Specialties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProfessionalChangeDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_ProfessionalChangeDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfessionalChangeDocuments_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProfessionalChangeDocuments_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProfessionalChangeDocuments_DoctorProfessionalChanges_RequestId",
                        column: x => x.RequestId,
                        principalTable: "DoctorProfessionalChanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProfessionalChangeEvidenceAccesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ViewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfessionalChangeEvidenceAccesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfessionalChangeEvidenceAccesses_AspNetUsers_ViewerUserId",
                        column: x => x.ViewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProfessionalChangeEvidenceAccesses_ProfessionalChangeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "ProfessionalChangeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfessionalChanges_CreatedByUserId",
                table: "DoctorProfessionalChanges",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfessionalChanges_DoctorProfileId",
                table: "DoctorProfessionalChanges",
                column: "DoctorProfileId",
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfessionalChanges_DoctorProfileId_UserId",
                table: "DoctorProfessionalChanges",
                columns: new[] { "DoctorProfileId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfessionalChanges_LastModifiedByUserId",
                table: "DoctorProfessionalChanges",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfessionalChanges_OriginalSpecialtyId",
                table: "DoctorProfessionalChanges",
                column: "OriginalSpecialtyId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfessionalChanges_ReviewerUserId",
                table: "DoctorProfessionalChanges",
                column: "ReviewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfessionalChanges_SpecialtyId",
                table: "DoctorProfessionalChanges",
                column: "SpecialtyId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfessionalChanges_Status_CreatedAtUtc_Id",
                table: "DoctorProfessionalChanges",
                columns: new[] { "Status", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionalChangeDocuments_CreatedByUserId",
                table: "ProfessionalChangeDocuments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionalChangeDocuments_LastModifiedByUserId",
                table: "ProfessionalChangeDocuments",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionalChangeDocuments_RequestId_Kind",
                table: "ProfessionalChangeDocuments",
                columns: new[] { "RequestId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionalChangeDocuments_StorageKey",
                table: "ProfessionalChangeDocuments",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionalChangeEvidenceAccesses_DocumentId_GrantedAtUtc",
                table: "ProfessionalChangeEvidenceAccesses",
                columns: new[] { "DocumentId", "GrantedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionalChangeEvidenceAccesses_ViewerUserId",
                table: "ProfessionalChangeEvidenceAccesses",
                column: "ViewerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfessionalChangeEvidenceAccesses");

            migrationBuilder.DropTable(
                name: "ProfessionalChangeDocuments");

            migrationBuilder.DropTable(
                name: "DoctorProfessionalChanges");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_DoctorProfiles_Id_UserId",
                table: "DoctorProfiles");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "DoctorProfiles");
        }
    }
}
