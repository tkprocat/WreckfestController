using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WreckfestController.Data.Migrations
{
    /// <inheritdoc />
    public partial class RaceResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Races",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrackId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Laps = table.Column<int>(type: "INTEGER", nullable: false),
                    GameMode = table.Column<int>(type: "INTEGER", nullable: false),
                    EventCounter = table.Column<int>(type: "INTEGER", nullable: false),
                    CupId = table.Column<int>(type: "INTEGER", nullable: true),
                    CupName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CupActivatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Races", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Races_Cups_CupId",
                        column: x => x.CupId,
                        principalTable: "Cups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RaceEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                    IsBot = table.Column<bool>(type: "INTEGER", nullable: false),
                    SteamId = table.Column<long>(type: "INTEGER", nullable: true),
                    VehicleKey = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                    VehicleName = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ClassIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Rating = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeMs = table.Column<int>(type: "INTEGER", nullable: true),
                    BestLapMs = table.Column<int>(type: "INTEGER", nullable: true),
                    CupPointsTotal = table.Column<int>(type: "INTEGER", nullable: false),
                    Slot = table.Column<int>(type: "INTEGER", nullable: false),
                    Lap = table.Column<int>(type: "INTEGER", nullable: false),
                    CarFlags = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishMs = table.Column<int>(type: "INTEGER", nullable: true),
                    PlayerStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerFlags = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaceEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaceEntries_Races_RaceId",
                        column: x => x.RaceId,
                        principalTable: "Races",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RaceEntries_RaceId",
                table: "RaceEntries",
                column: "RaceId");

            migrationBuilder.CreateIndex(
                name: "IX_RaceEntries_SteamId",
                table: "RaceEntries",
                column: "SteamId");

            migrationBuilder.CreateIndex(
                name: "IX_RaceEntries_VehicleKey",
                table: "RaceEntries",
                column: "VehicleKey");

            migrationBuilder.CreateIndex(
                name: "IX_Races_CupId_CupActivatedAt",
                table: "Races",
                columns: new[] { "CupId", "CupActivatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Races_EndedAt",
                table: "Races",
                column: "EndedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Races_TrackId_EndedAt",
                table: "Races",
                columns: new[] { "TrackId", "EndedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RaceEntries");

            migrationBuilder.DropTable(
                name: "Races");
        }
    }
}
