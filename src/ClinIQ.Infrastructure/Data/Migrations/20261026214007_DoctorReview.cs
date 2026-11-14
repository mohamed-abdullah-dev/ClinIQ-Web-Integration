using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DoctorReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                table: "DoctorRegistrations",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateTable(
                name: "DoctorEvidenceAccesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ViewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorEvidenceAccesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DoctorEvidenceAccesses_AspNetUsers_ViewerUserId",
                        column: x => x.ViewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorEvidenceAccesses_DoctorEvidenceDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "DoctorEvidenceDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DoctorProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorRegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfessionalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SpecialtyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfessionalGrade = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DoctorProfiles_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorProfiles_AspNetUsers_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorProfiles_DoctorRegistrations_DoctorRegistrationId",
                        column: x => x.DoctorRegistrationId,
                        principalTable: "DoctorRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorProfiles_Specialties_SpecialtyId",
                        column: x => x.SpecialtyId,
                        principalTable: "Specialties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DoctorRegistrationReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorRegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorRegistrationReviews", x => x.Id);
                    table.CheckConstraint("CK_DoctorRegistrationReviews_Outcome", "[Outcome] IN (2, 3)");
                    table.ForeignKey(
                        name: "FK_DoctorRegistrationReviews_AspNetUsers_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DoctorRegistrationReviews_DoctorRegistrations_DoctorRegistrationId",
                        column: x => x.DoctorRegistrationId,
                        principalTable: "DoctorRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorRegistrations_Status_CreatedAtUtc_Id",
                table: "DoctorRegistrations",
                columns: new[] { "Status", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorEvidenceAccesses_DocumentId_GrantedAtUtc",
                table: "DoctorEvidenceAccesses",
                columns: new[] { "DocumentId", "GrantedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorEvidenceAccesses_ViewerUserId",
                table: "DoctorEvidenceAccesses",
                column: "ViewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfiles_CreatedByUserId",
                table: "DoctorProfiles",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfiles_DoctorRegistrationId",
                table: "DoctorProfiles",
                column: "DoctorRegistrationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfiles_LastModifiedByUserId",
                table: "DoctorProfiles",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfiles_SpecialtyId",
                table: "DoctorProfiles",
                column: "SpecialtyId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorProfiles_UserId",
                table: "DoctorProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DoctorRegistrationReviews_DoctorRegistrationId",
                table: "DoctorRegistrationReviews",
                column: "DoctorRegistrationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DoctorRegistrationReviews_ReviewerUserId",
                table: "DoctorRegistrationReviews",
                column: "ReviewerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DoctorEvidenceAccesses");

            migrationBuilder.DropTable(
                name: "DoctorProfiles");

            migrationBuilder.DropTable(
                name: "DoctorRegistrationReviews");

            migrationBuilder.DropIndex(
                name: "IX_DoctorRegistrations_Status_CreatedAtUtc_Id",
                table: "DoctorRegistrations");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "DoctorRegistrations");
        }
    }
}
