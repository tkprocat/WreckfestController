using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WreckfestController.Data.Migrations
{
    /// <summary>
    /// Scheduled events become cups (#135), keeping every row, and a cup gains its scoring:
    /// <c>session_mode</c> and <c>grid_order</c>. Renames, not the drop-and-create EF
    /// scaffolds, so existing cups and their occurrence history survive.
    /// </summary>
    /// <remarks>
    /// SQLite cannot rename an index, so each is dropped and created under its new name.
    /// The primary and foreign key constraint names inside the renamed tables keep their
    /// old spelling; SQLite does not use them, and EF does not look them up.
    /// </remarks>
    public partial class RenameScheduledEventsToCups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            DropIndexes(migrationBuilder, "ScheduledEvents", "EventOccurrences", "ScheduledEventId");

            migrationBuilder.RenameTable(name: "ScheduledEvents", newName: "Cups");
            migrationBuilder.RenameTable(name: "EventOccurrences", newName: "CupOccurrences");
            migrationBuilder.RenameColumn(name: "ScheduledEventId", table: "CupOccurrences", newName: "CupId");

            CreateIndexes(migrationBuilder, "Cups", "CupOccurrences", "CupId");

            migrationBuilder.AddColumn<string>(
                name: "SessionMode",
                table: "Cups",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GridOrder",
                table: "Cups",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "GridOrder", table: "Cups");
            migrationBuilder.DropColumn(name: "SessionMode", table: "Cups");

            DropIndexes(migrationBuilder, "Cups", "CupOccurrences", "CupId");

            migrationBuilder.RenameColumn(name: "CupId", table: "CupOccurrences", newName: "ScheduledEventId");
            migrationBuilder.RenameTable(name: "CupOccurrences", newName: "EventOccurrences");
            migrationBuilder.RenameTable(name: "Cups", newName: "ScheduledEvents");

            CreateIndexes(migrationBuilder, "ScheduledEvents", "EventOccurrences", "ScheduledEventId");
        }

        private static void DropIndexes(MigrationBuilder migrationBuilder, string table, string occurrences, string foreignKey)
        {
            migrationBuilder.DropIndex(name: $"IX_{table}_CollectionId", table: table);
            migrationBuilder.DropIndex(name: $"IX_{table}_CreatedById", table: table);
            migrationBuilder.DropIndex(name: $"IX_{table}_IsActive", table: table);
            migrationBuilder.DropIndex(name: $"IX_{table}_NextOccurrence", table: table);
            migrationBuilder.DropIndex(name: $"IX_{occurrences}_{foreignKey}_Occurrence", table: occurrences);
        }

        private static void CreateIndexes(MigrationBuilder migrationBuilder, string table, string occurrences, string foreignKey)
        {
            migrationBuilder.CreateIndex(
                name: $"IX_{table}_CollectionId",
                table: table,
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: $"IX_{table}_CreatedById",
                table: table,
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: $"IX_{table}_IsActive",
                table: table,
                column: "IsActive",
                unique: true,
                filter: "\"IsActive\" = 1");

            migrationBuilder.CreateIndex(
                name: $"IX_{table}_NextOccurrence",
                table: table,
                column: "NextOccurrence");

            migrationBuilder.CreateIndex(
                name: $"IX_{occurrences}_{foreignKey}_Occurrence",
                table: occurrences,
                columns: new[] { foreignKey, "Occurrence" },
                unique: true);
        }
    }
}
