using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WreckfestController.Data.Migrations
{
    /// <inheritdoc />
    public partial class RaceRunIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Races_CupActivatedAt_CupPhase",
                table: "Races",
                columns: new[] { "CupActivatedAt", "CupPhase" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Races_CupActivatedAt_CupPhase",
                table: "Races");
        }
    }
}
