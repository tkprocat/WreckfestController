using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WreckfestController.Data.Migrations
{
    /// <inheritdoc />
    public partial class CupPhases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CupPhase",
                table: "Races",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CurrentEnd",
                table: "Cups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CurrentOccurrence",
                table: "Cups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "EndTime",
                table: "Cups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phase",
                table: "Cups",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RestartRotationAtStart",
                table: "Cups",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "WarmupTime",
                table: "Cups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CupPointsOff",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CupName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Since = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CupPointsOff", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CupPointsOff");

            migrationBuilder.DropColumn(
                name: "CupPhase",
                table: "Races");

            migrationBuilder.DropColumn(
                name: "CurrentEnd",
                table: "Cups");

            migrationBuilder.DropColumn(
                name: "CurrentOccurrence",
                table: "Cups");

            migrationBuilder.DropColumn(
                name: "EndTime",
                table: "Cups");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "Cups");

            migrationBuilder.DropColumn(
                name: "RestartRotationAtStart",
                table: "Cups");

            migrationBuilder.DropColumn(
                name: "WarmupTime",
                table: "Cups");
        }
    }
}
