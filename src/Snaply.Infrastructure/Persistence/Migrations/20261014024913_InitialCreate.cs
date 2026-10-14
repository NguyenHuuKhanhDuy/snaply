using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snaply.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Screenshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ThumbnailPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    OcrText = table.Column<string>(type: "TEXT", nullable: true),
                    OcrLanguage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedByCaptureMethod = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Screenshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "NOCASE"),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnnotationDocuments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScreenshotId = table.Column<long>(type: "INTEGER", nullable: false),
                    DocumentVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentData = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnotationDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnnotationDocuments_Screenshots_ScreenshotId",
                        column: x => x.ScreenshotId,
                        principalTable: "Screenshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScreenshotTags",
                columns: table => new
                {
                    ScreenshotId = table.Column<long>(type: "INTEGER", nullable: false),
                    TagId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreenshotTags", x => new { x.ScreenshotId, x.TagId });
                    table.ForeignKey(
                        name: "FK_ScreenshotTags_Screenshots_ScreenshotId",
                        column: x => x.ScreenshotId,
                        principalTable: "Screenshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScreenshotTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnnotationDocuments_ScreenshotId",
                table: "AnnotationDocuments",
                column: "ScreenshotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_CreatedAt_Id",
                table: "Screenshots",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_FileName",
                table: "Screenshots",
                column: "FileName");

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_FilePath",
                table: "Screenshots",
                column: "FilePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_IsFavorite",
                table: "Screenshots",
                column: "IsFavorite");

            migrationBuilder.CreateIndex(
                name: "IX_ScreenshotTags_TagId",
                table: "ScreenshotTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnnotationDocuments");

            migrationBuilder.DropTable(
                name: "ApplicationSettings");

            migrationBuilder.DropTable(
                name: "ScreenshotTags");

            migrationBuilder.DropTable(
                name: "Screenshots");

            migrationBuilder.DropTable(
                name: "Tags");
        }
    }
}
