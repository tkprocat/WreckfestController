using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WreckfestController.Data.Migrations
{
    /// <inheritdoc />
    public partial class RaceEntryBestLapSetAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "FinishMs",
                table: "RaceEntries",
                newName: "BestLapSetAtMs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "BestLapSetAtMs",
                table: "RaceEntries",
                newName: "FinishMs");
        }
    }
}
