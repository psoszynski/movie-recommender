using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieRecommender.Migrations;

/// <inheritdoc />
public partial class _20261126113138_AddMediaTypeAndSource : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Movies_TmdbId",
            table: "Movies");

        migrationBuilder.AddColumn<string>(
            name: "MediaType",
            table: "Movies",
            type: "nvarchar(10)",
            maxLength: 10,
            nullable: false,
            defaultValue: "movie");

        migrationBuilder.AddColumn<string>(
            name: "Source",
            table: "Movies",
            type: "nvarchar(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "movielens");

        migrationBuilder.CreateIndex(
            name: "IX_Movies_MediaType_TmdbId",
            table: "Movies",
            columns: new[] { "MediaType", "TmdbId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Movies_MediaType_TmdbId",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "MediaType",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "Source",
            table: "Movies");

        migrationBuilder.CreateIndex(
            name: "IX_Movies_TmdbId",
            table: "Movies",
            column: "TmdbId");
    }
}
