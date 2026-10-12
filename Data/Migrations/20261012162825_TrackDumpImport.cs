using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WreckfestController.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackDumpImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "TrackLengthM",
                table: "TrackVariants",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DumpId",
                table: "Tracks",
                type: "TEXT",
                maxLength: 200,
                nullable: true,
                collation: "NOCASE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TrackLengthM",
                table: "TrackVariants");

            migrationBuilder.DropColumn(
                name: "DumpId",
                table: "Tracks");
        }
    }
}
