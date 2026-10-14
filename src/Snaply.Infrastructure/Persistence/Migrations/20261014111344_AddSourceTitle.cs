using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snaply.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceTitle",
                table: "Screenshots",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceTitle",
                table: "Screenshots");
        }
    }
}
