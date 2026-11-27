using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndxServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyPrivateCold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PrivateCold",
                table: "ApiKeys",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrivateCold",
                table: "ApiKeys");
        }
    }
}
