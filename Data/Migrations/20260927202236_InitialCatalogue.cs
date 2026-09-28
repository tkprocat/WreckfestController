using Microsoft.EntityFrameworkCore.Migrations;
using WreckfestController.Data.Catalogue;

#nullable disable

namespace WreckfestController.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Mods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FolderName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE"),
                    WorkshopId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    Color = table.Column<string>(type: "TEXT", maxLength: 7, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WeatherConditions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, collation: "NOCASE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherConditions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DlcName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ModId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tracks_Mods_ModId",
                        column: x => x.ModId,
                        principalTable: "Mods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TrackVariants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VariantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    GameMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TrackId = table.Column<int>(type: "INTEGER", nullable: false),
                    AllowedForVoting = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackVariants_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackWeatherConditions",
                columns: table => new
                {
                    TrackId = table.Column<int>(type: "INTEGER", nullable: false),
                    WeatherConditionId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackWeatherConditions", x => new { x.TrackId, x.WeatherConditionId });
                    table.ForeignKey(
                        name: "FK_TrackWeatherConditions_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrackWeatherConditions_WeatherConditions_WeatherConditionId",
                        column: x => x.WeatherConditionId,
                        principalTable: "WeatherConditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackVariantTags",
                columns: table => new
                {
                    TrackVariantId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackVariantTags", x => new { x.TrackVariantId, x.TagId });
                    table.ForeignKey(
                        name: "FK_TrackVariantTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrackVariantTags_TrackVariants_TrackVariantId",
                        column: x => x.TrackVariantId,
                        principalTable: "TrackVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Mods_FolderName",
                table: "Mods",
                column: "FolderName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Slug",
                table: "Tags",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Key",
                table: "Tracks",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_ModId",
                table: "Tracks",
                column: "ModId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackVariants_TrackId",
                table: "TrackVariants",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackVariants_VariantId",
                table: "TrackVariants",
                column: "VariantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackVariantTags_TagId",
                table: "TrackVariantTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackWeatherConditions_WeatherConditionId",
                table: "TrackWeatherConditions",
                column: "WeatherConditionId");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherConditions_Name",
                table: "WeatherConditions",
                column: "Name",
                unique: true);

            InsertCatalogue(migrationBuilder);
        }

        /// <summary>
        /// Inserts the shipped catalogue once, with InsertData rather than HasData: HasData
        /// would turn any later change to a shipped value into an UPDATE that overwrites
        /// the admin's edits to that row.
        /// </summary>
        private static void InsertCatalogue(MigrationBuilder migrationBuilder)
        {
            var weather = InitialCatalogueData.Weather;
            var weatherIds = weather.Select((name, i) => (name, id: i + 1))
                .ToDictionary(w => w.name, w => w.id, StringComparer.OrdinalIgnoreCase);
            var tags = InitialCatalogueData.Tags;
            var tagIds = tags.Select((tag, i) => (tag.Slug, id: i + 1))
                .ToDictionary(t => t.Slug, t => t.id, StringComparer.OrdinalIgnoreCase);

            migrationBuilder.InsertData(
                table: "WeatherConditions",
                columns: ["Id", "Name"],
                values: Rows(weather.Select((name, i) => new object[] { i + 1, name })));

            migrationBuilder.InsertData(
                table: "Tags",
                columns: ["Id", "Name", "Slug", "Color"],
                values: Rows(tags.Select((tag, i) => new object[] { i + 1, tag.Name, tag.Slug, tag.Color })));

            var tracks = new List<object[]>();
            var trackWeather = new List<object[]>();
            var variants = new List<object[]>();
            var variantTags = new List<object[]>();
            foreach (var (track, trackIndex) in InitialCatalogueData.Tracks.Select((t, i) => (t, i)))
            {
                var trackId = trackIndex + 1;
                tracks.Add([trackId, track.Key, track.Name, track.Origin.ToString(), null, null, true, false, 1]);
                trackWeather.AddRange(track.Weather.Select(w => new object[] { trackId, weatherIds[w] }));

                foreach (var variant in track.Variants)
                {
                    var variantId = variants.Count + 1;
                    variants.Add([
                        variantId, variant.VariantId, variant.Name, variant.GameMode.ToString(), trackId,
                        variant.AllowedForVoting, true, false, 1,
                    ]);
                    variantTags.AddRange(variant.Tags.Select(t => new object[] { variantId, tagIds[t] }));
                }
            }

            migrationBuilder.InsertData(
                table: "Tracks",
                columns: ["Id", "Key", "Name", "Origin", "DlcName", "ModId", "IsBuiltIn", "IsHidden", "Version"],
                values: Rows(tracks));

            migrationBuilder.InsertData(
                table: "TrackWeatherConditions",
                columns: ["TrackId", "WeatherConditionId"],
                values: Rows(trackWeather));

            migrationBuilder.InsertData(
                table: "TrackVariants",
                columns: [
                    "Id", "VariantId", "Name", "GameMode", "TrackId",
                    "AllowedForVoting", "IsBuiltIn", "IsHidden", "Version",
                ],
                values: Rows(variants));

            migrationBuilder.InsertData(
                table: "TrackVariantTags",
                columns: ["TrackVariantId", "TagId"],
                values: Rows(variantTags));
        }

        private static object[,] Rows(IEnumerable<object[]> rows)
        {
            var list = rows.ToList();
            var result = new object[list.Count, list[0].Length];
            for (var row = 0; row < list.Count; row++)
            {
                for (var column = 0; column < list[row].Length; column++)
                {
                    result[row, column] = list[row][column];
                }
            }

            return result;
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackVariantTags");

            migrationBuilder.DropTable(
                name: "TrackWeatherConditions");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "TrackVariants");

            migrationBuilder.DropTable(
                name: "WeatherConditions");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropTable(
                name: "Mods");
        }
    }
}
