using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndxCloudApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLastTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastTeamId",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTeamId",
                table: "AspNetUsers");
        }
    }
}
