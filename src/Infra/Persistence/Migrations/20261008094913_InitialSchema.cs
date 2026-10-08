using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FaceAttendance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Department = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    EnrollmentStatus = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EnrolledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Decision = table.Column<int>(type: "int", nullable: false),
                    MatchScore = table.Column<double>(type: "float", nullable: false),
                    Threshold = table.Column<double>(type: "float", nullable: false),
                    MatchedPose = table.Column<int>(type: "int", nullable: true),
                    MatchedTemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProviderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProbeImageObjectKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    DeviceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessingMs = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceEvents_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FaceTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Pose = table.Column<int>(type: "int", nullable: false),
                    ProviderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Dimension = table.Column<int>(type: "int", nullable: false),
                    TemplateData = table.Column<byte[]>(type: "varbinary(max)", maxLength: 8192, nullable: false),
                    QualityScore = table.Column<double>(type: "float", nullable: false),
                    DetectionScore = table.Column<double>(type: "float", nullable: false),
                    ImageObjectKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ImageContentType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ImageSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ImageSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ImageWidth = table.Column<int>(type: "int", nullable: false),
                    ImageHeight = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FaceTemplates_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceEvents_CreatedAt",
                table: "AttendanceEvents",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceEvents_Person_CreatedAt",
                table: "AttendanceEvents",
                columns: new[] { "PersonId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceEvents_Tenant_CreatedAt",
                table: "AttendanceEvents",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FaceTemplates_Person_Active_Pose",
                table: "FaceTemplates",
                columns: new[] { "PersonId", "IsActive", "Pose" });

            migrationBuilder.CreateIndex(
                name: "IX_FaceTemplates_Person_ImageHash",
                table: "FaceTemplates",
                columns: new[] { "PersonId", "ImageSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_FaceTemplates_Provider_Active",
                table: "FaceTemplates",
                columns: new[] { "ProviderId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "UX_FaceTemplates_Person_Pose_Active",
                table: "FaceTemplates",
                columns: new[] { "PersonId", "Pose" },
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_People_Tenant_Enrollment_Status",
                table: "People",
                columns: new[] { "TenantId", "EnrollmentStatus", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_People_Tenant_ExternalId",
                table: "People",
                columns: new[] { "TenantId", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceEvents");

            migrationBuilder.DropTable(
                name: "FaceTemplates");

            migrationBuilder.DropTable(
                name: "People");
        }
    }
}
