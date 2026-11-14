using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinIQ.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FeedbackNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLCMD may start with QUOTED_IDENTIFIER OFF; filtered indexes require these session settings.
            migrationBuilder.Sql("SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF;");
            migrationBuilder.AddUniqueConstraint(
                name: "AK_AuthSessions_Id_UserId",
                table: "AuthSessions",
                columns: new[] { "Id", "UserId" });

            migrationBuilder.CreateTable(
                name: "FeedbackNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FeedbackRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpandedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastErrorCode = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackNotices", x => x.Id);
                    table.CheckConstraint("CK_FeedbackNotices_Kind", "[Kind] IN (1,2) AND [NextAttemptAtUtc]>=[DueAtUtc]");
                    table.ForeignKey(
                        name: "FK_FeedbackNotices_AttendanceFeedbackRequests_FeedbackRequestId",
                        column: x => x.FeedbackRequestId,
                        principalTable: "AttendanceFeedbackRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PushDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstallationSecretHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TokenHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    ProtectedToken = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: true),
                    Platform = table.Column<int>(type: "int", nullable: false),
                    BindingRevision = table.Column<int>(type: "int", nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDevices", x => x.Id);
                    table.CheckConstraint("CK_PushDevices_Binding", "[Platform] IN (1,2) AND [BindingRevision]>0 AND LEN([InstallationSecretHash])=64 AND (([UserId] IS NULL AND [SessionId] IS NULL AND [TokenHash] IS NULL AND [ProtectedToken] IS NULL) OR ([UserId] IS NOT NULL AND [SessionId] IS NOT NULL AND [TokenHash] IS NOT NULL AND [ProtectedToken] IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_PushDevices_AuthSessions_SessionId_UserId",
                        columns: x => new { x.SessionId, x.UserId },
                        principalTable: "AuthSessions",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FeedbackDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NoticeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BindingRevision = table.Column<int>(type: "int", nullable: true),
                    TargetKey = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastErrorCode = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackDeliveries", x => x.Id);
                    table.CheckConstraint("CK_FeedbackDeliveries_State", "[State] BETWEEN 1 AND 6 AND [Attempts]>=0 AND (([DeviceId] IS NULL AND [SessionId] IS NULL AND [BindingRevision] IS NULL AND [TargetKey]='email') OR ([DeviceId] IS NOT NULL AND [SessionId] IS NOT NULL AND [BindingRevision]>0)) AND ([State]<>2 OR ([ClaimId] IS NOT NULL AND [LeaseUntilUtc] IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_FeedbackDeliveries_AspNetUsers_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeedbackDeliveries_AuthSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "AuthSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeedbackDeliveries_FeedbackNotices_NoticeId",
                        column: x => x.NoticeId,
                        principalTable: "FeedbackNotices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeedbackDeliveries_PushDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "PushDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackDeliveries_DeviceId",
                table: "FeedbackDeliveries",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackDeliveries_NoticeId_TargetKey",
                table: "FeedbackDeliveries",
                columns: new[] { "NoticeId", "TargetKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackDeliveries_RecipientUserId",
                table: "FeedbackDeliveries",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackDeliveries_SessionId",
                table: "FeedbackDeliveries",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackDeliveries_State_NextAttemptAtUtc",
                table: "FeedbackDeliveries",
                columns: new[] { "State", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackNotices_ExpandedAtUtc_CancelledAtUtc_NextAttemptAtUtc",
                table: "FeedbackNotices",
                columns: new[] { "ExpandedAtUtc", "CancelledAtUtc", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackNotices_FeedbackRequestId_Kind",
                table: "FeedbackNotices",
                columns: new[] { "FeedbackRequestId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_SessionId_UserId",
                table: "PushDevices",
                columns: new[] { "SessionId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_TokenHash",
                table: "PushDevices",
                column: "TokenHash",
                unique: true,
                filter: "[TokenHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_UserId",
                table: "PushDevices",
                column: "UserId");
            migrationBuilder.Sql("""
                INSERT INTO FeedbackNotices(Id,FeedbackRequestId,Kind,DueAtUtc,NextAttemptAtUtc)
                SELECT NEWID(),r.Id,k.Kind,DATEADD(hour,k.Hours,r.CreatedAtUtc),DATEADD(hour,k.Hours,r.CreatedAtUtc)
                FROM AttendanceFeedbackRequests r CROSS JOIN (VALUES(1,0),(2,24)) k(Kind,Hours);
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER TR_AuthSessions_DetachPush ON AuthSessions AFTER UPDATE AS
                BEGIN SET NOCOUNT ON;
                    UPDATE d SET UserId=NULL,SessionId=NULL,TokenHash=NULL,ProtectedToken=NULL,
                        BindingRevision=d.BindingRevision+1,ChangedAtUtc=s.RevokedAtUtc
                    FROM PushDevices d JOIN inserted s ON s.Id=d.SessionId WHERE s.RevokedAtUtc IS NOT NULL;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS(SELECT 1 FROM Appointments) OR EXISTS(SELECT 1 FROM PushDevices) OR EXISTS(SELECT 1 FROM FeedbackNotices)
                    THROW 51015, 'Refusing to erase notification bindings or recorded booking notification intent.', 1;
                DROP TRIGGER TR_AuthSessions_DetachPush;
                """);
            migrationBuilder.DropTable(
                name: "FeedbackDeliveries");

            migrationBuilder.DropTable(
                name: "FeedbackNotices");

            migrationBuilder.DropTable(
                name: "PushDevices");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AuthSessions_Id_UserId",
                table: "AuthSessions");
        }
    }
}
