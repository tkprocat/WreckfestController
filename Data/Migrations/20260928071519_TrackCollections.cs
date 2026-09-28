using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WreckfestController.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackCollections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackCollections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrackCollectionEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackVariantId = table.Column<int>(type: "INTEGER", nullable: true),
                    TrackId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    Gamemode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Laps = table.Column<int>(type: "INTEGER", nullable: true),
                    Bots = table.Column<int>(type: "INTEGER", nullable: true),
                    NumTeams = table.Column<int>(type: "INTEGER", nullable: true),
                    CarResetDisabled = table.Column<bool>(type: "INTEGER", nullable: true),
                    WrongWayLimiterDisabled = table.Column<bool>(type: "INTEGER", nullable: true),
                    CarClassRestriction = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CarRestriction = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Weather = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackCollectionEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackCollectionEntries_TrackCollections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "TrackCollections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrackCollectionEntries_TrackVariants_TrackVariantId",
                        column: x => x.TrackVariantId,
                        principalTable: "TrackVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackCollectionEntries_CollectionId_Position",
                table: "TrackCollectionEntries",
                columns: new[] { "CollectionId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackCollectionEntries_TrackVariantId",
                table: "TrackCollectionEntries",
                column: "TrackVariantId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackCollections_Name",
                table: "TrackCollections",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackCollectionEntries");

            migrationBuilder.DropTable(
                name: "TrackCollections");
        }
    }
}
