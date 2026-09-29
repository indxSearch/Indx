using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndxServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyReveal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KeySuffix",
                table: "ApiKeys",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SealedToken",
                table: "ApiKeys",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeySuffix",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "SealedToken",
                table: "ApiKeys");
        }
    }
}
