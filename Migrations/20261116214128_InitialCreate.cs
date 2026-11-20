using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieRecommender.Migrations;

/// <inheritdoc />
public partial class _20261116214128_InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Movies",
            columns: table => new
            {
                MovieId = table.Column<int>(type: "int", nullable: false),
                Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                Year = table.Column<int>(type: "int", nullable: true),
                Genres = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                TmdbId = table.Column<int>(type: "int", nullable: true),
                Overview = table.Column<string>(type: "nvarchar(max)", nullable: true),
                AvgRating = table.Column<double>(type: "float", nullable: true),
                RatingCount = table.Column<int>(type: "int", nullable: false),
                EmbeddingText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                EmbeddingModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                Embedding = table.Column<SqlVector<float>>(type: "vector(768)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Movies", x => x.MovieId);
            });

        migrationBuilder.CreateTable(
            name: "Ratings",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<int>(type: "int", nullable: false),
                MovieId = table.Column<int>(type: "int", nullable: false),
                Stars = table.Column<float>(type: "real", nullable: false),
                RatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Ratings", x => x.Id);
                table.ForeignKey(
                    name: "FK_Ratings_Movies_MovieId",
                    column: x => x.MovieId,
                    principalTable: "Movies",
                    principalColumn: "MovieId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Tags",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<int>(type: "int", nullable: false),
                MovieId = table.Column<int>(type: "int", nullable: false),
                Text = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tags", x => x.Id);
                table.ForeignKey(
                    name: "FK_Tags_Movies_MovieId",
                    column: x => x.MovieId,
                    principalTable: "Movies",
                    principalColumn: "MovieId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Movies_AvgRating",
            table: "Movies",
            column: "AvgRating");

        migrationBuilder.CreateIndex(
            name: "IX_Movies_TmdbId",
            table: "Movies",
            column: "TmdbId");

        migrationBuilder.CreateIndex(
            name: "IX_Movies_Year",
            table: "Movies",
            column: "Year");

        migrationBuilder.CreateIndex(
            name: "IX_Ratings_MovieId",
            table: "Ratings",
            column: "MovieId");

        migrationBuilder.CreateIndex(
            name: "IX_Ratings_UserId_MovieId",
            table: "Ratings",
            columns: new[] { "UserId", "MovieId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Tags_MovieId",
            table: "Tags",
            column: "MovieId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Ratings");

        migrationBuilder.DropTable(
            name: "Tags");

        migrationBuilder.DropTable(
            name: "Movies");
    }
}
