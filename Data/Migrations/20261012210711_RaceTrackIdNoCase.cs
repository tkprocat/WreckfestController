using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WreckfestController.Data.Migrations
{
    /// <summary>
    /// Races by track id ignoring case (#261): the public track pages look a layout's races up
    /// as <c>TrackId COLLATE NOCASE = @id</c>, since the log keeps the id as the game sent
    /// it. The model does not describe a collated index, so it is raw SQL, and the snapshot
    /// is unchanged.
    /// </summary>
    public partial class RaceTrackIdNoCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE INDEX \"IX_Races_TrackId_NoCase\" ON \"Races\" (\"TrackId\" COLLATE NOCASE, \"EndedAt\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX \"IX_Races_TrackId_NoCase\";");
        }
    }
}
