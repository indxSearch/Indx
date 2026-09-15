using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndxServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Datasets",
                table: "ApiKeys",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Level",
                table: "ApiKeys",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "Full");

            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                table: "ApiKeys",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Datasets",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "ApiKeys");
        }
    }
}
