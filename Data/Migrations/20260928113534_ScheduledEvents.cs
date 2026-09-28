using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WreckfestController.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    StartTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TimeZone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Repeat = table.Column<string>(type: "TEXT", nullable: true),
                    ServerConfig = table.Column<string>(type: "TEXT", nullable: true),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Tracks = table.Column<string>(type: "TEXT", nullable: false),
                    CollectionName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedById = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    NextOccurrence = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOccurrence = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOutcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledEvents_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ScheduledEvents_TrackCollections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "TrackCollections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "EventOccurrences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScheduledEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    Occurrence = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventOccurrences_ScheduledEvents_ScheduledEventId",
                        column: x => x.ScheduledEventId,
                        principalTable: "ScheduledEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventOccurrences_ScheduledEventId_Occurrence",
                table: "EventOccurrences",
                columns: new[] { "ScheduledEventId", "Occurrence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledEvents_CollectionId",
                table: "ScheduledEvents",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledEvents_CreatedById",
                table: "ScheduledEvents",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledEvents_IsActive",
                table: "ScheduledEvents",
                column: "IsActive",
                unique: true,
                filter: "\"IsActive\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledEvents_NextOccurrence",
                table: "ScheduledEvents",
                column: "NextOccurrence");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventOccurrences");

            migrationBuilder.DropTable(
                name: "ScheduledEvents");
        }
    }
}
