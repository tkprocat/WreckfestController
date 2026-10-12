using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WreckfestController.Data.Migrations
{
    /// <inheritdoc />
    public partial class CatalogueImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogueImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrackId = table.Column<int>(type: "INTEGER", nullable: true),
                    TrackVariantId = table.Column<int>(type: "INTEGER", nullable: true),
                    Size = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IsOverlay = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogueImages", x => x.Id);
                    table.CheckConstraint("CK_CatalogueImages_Owner", "(\"TrackId\" IS NULL) <> (\"TrackVariantId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_CatalogueImages_TrackVariants_TrackVariantId",
                        column: x => x.TrackVariantId,
                        principalTable: "TrackVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CatalogueImages_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogueImages_TrackId_Size_Origin",
                table: "CatalogueImages",
                columns: new[] { "TrackId", "Size", "Origin" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogueImages_TrackVariantId_Size_Origin",
                table: "CatalogueImages",
                columns: new[] { "TrackVariantId", "Size", "Origin" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogueImages");
        }
    }
}
