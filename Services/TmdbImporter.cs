using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieRecommender.Data;
using MovieRecommender.Models;

namespace MovieRecommender.Services;

public sealed record TmdbImportOptions(
    int MovieFromYear,
    int MinMovieVotes,
    int MinTvVotes,
    int MaxPagesPerSlice,
    bool IncludeMovies,
    bool IncludeTv);

/// <summary>
/// Optional step: add popular titles that MovieLens doesn't have, straight from TMDB's
/// /discover endpoints. Movies are limited to a release year and later; series have no
/// date limit. "Popular" = at least N votes, sorted by TMDB popularity.
///
/// Notes:
///  * /discover returns at most 500 pages (10k results) per query, so we slice the query by
///    release month (movies) or first-air year (series).
///  * Overviews arrive with the discover results, so these rows skip the `enrich` step and
///    go straight to `embed`. Titles without an overview are skipped: they can't be embedded.
///  * TMDB movie and TV ids overlap, so MovieId is offset (movies 10M+, series 20M+) to stay
///    clear of MovieLens ids and of each other. MediaType + TmdbId identify a title.
///  * There are no per-user ratings: AvgRating is vote_average/2 (0–5 scale) and
///    RatingCount is vote_count. Source = "tmdb" marks these rows.
///  * Idempotent: titles already in the database (including MovieLens ones) are skipped.
/// </summary>
public sealed class TmdbImporter(MovieDbContext db, IHttpClientFactory httpFactory, ILogger<TmdbImporter> log)
{
    private const string Base = "https://api.themoviedb.org/3";
    private const int MovieIdOffset = 10_000_000;
    private const int TvIdOffset = 20_000_000;
    private const int FirstTvYear = 1940;

    // MovieLens genre names, so TMDB titles filter the same way with --genre.
    private static readonly Dictionary<string, string[]> GenreAliases = new()
    {
        ["Science Fiction"] = ["Sci-Fi"],
        ["Action & Adventure"] = ["Action", "Adventure"],
        ["Sci-Fi & Fantasy"] = ["Sci-Fi", "Fantasy"],
        ["War & Politics"] = ["War"],
        ["Music"] = ["Musical"],
        ["Kids"] = ["Children"],
    };

    public async Task RunAsync(string apiKey, TmdbImportOptions o, CancellationToken ct)
    {
        var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var known = (await db.Movies.Where(m => m.TmdbId != null)
                .Select(m => new { m.MediaType, TmdbId = m.TmdbId!.Value }).ToListAsync(ct))
            .Select(m => (m.MediaType, m.TmdbId))
            .ToHashSet();

        var total = 0;
        if (o.IncludeMovies)
        {
            var genres = await LoadGenresAsync(http, apiKey, "movie", ct);
            var now = DateTime.UtcNow;
            for (var month = new DateTime(o.MovieFromYear, 1, 1); month <= now; month = month.AddMonths(1))
            {
                var from = month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var to = month.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var query = $"primary_release_date.gte={from}&primary_release_date.lte={to}&vote_count.gte={o.MinMovieVotes}";
                total += await ImportSliceAsync(http, apiKey, "movie", query, $"movies {month:yyyy-MM}", genres, known, o, ct);
            }
        }

        if (o.IncludeTv)
        {
            var genres = await LoadGenresAsync(http, apiKey, "tv", ct);
            for (var year = FirstTvYear; year <= DateTime.UtcNow.Year; year++)
            {
                var query = $"first_air_date.gte={year}-01-01&first_air_date.lte={year}-12-31&vote_count.gte={o.MinTvVotes}";
                total += await ImportSliceAsync(http, apiKey, "tv", query, $"series {year}", genres, known, o, ct);
            }
        }

        log.LogInformation("TMDB import complete: {Total} new titles. Run `embed` next.", total);
    }

    private async Task<int> ImportSliceAsync(
        HttpClient http, string apiKey, string type, string filter, string label,
        Dictionary<int, string> genres, HashSet<(string, int)> known, TmdbImportOptions o, CancellationToken ct)
    {
        var added = 0;
        var totalPages = 1;
        for (var page = 1; page <= Math.Min(totalPages, o.MaxPagesPerSlice); page++)
        {
            var url = $"{Base}/discover/{type}?api_key={apiKey}&language=en-US&include_adult=false&sort_by=popularity.desc&{filter}&page={page}";
            var result = await GetAsync<DiscoverPage>(http, url, ct);
            if (result is null) break;
            totalPages = result.TotalPages;

            foreach (var r in result.Results)
            {
                if (string.IsNullOrWhiteSpace(r.Overview) || !known.Add((type, r.Id))) continue;

                var title = (type == "movie" ? r.Title : r.Name) ?? r.OriginalTitle ?? r.OriginalName;
                if (string.IsNullOrWhiteSpace(title)) continue;
                var date = type == "movie" ? r.ReleaseDate : r.FirstAirDate;

                var genreNames = (r.GenreIds ?? [])
                    .Where(genres.ContainsKey)
                    .SelectMany(id => GenreAliases.TryGetValue(genres[id], out var a) ? a : [genres[id]])
                    .Distinct();

                db.Movies.Add(new Movie
                {
                    MovieId = (type == "movie" ? MovieIdOffset : TvIdOffset) + r.Id,
                    TmdbId = r.Id,
                    MediaType = type,
                    Source = "tmdb",
                    Title = Truncate(title.Trim(), 300),
                    Year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null,
                    Genres = Truncate(string.Join('|', genreNames), 200),
                    Overview = r.Overview.Trim(),
                    AvgRating = Math.Round(r.VoteAverage / 2, 2),
                    RatingCount = r.VoteCount,
                });
                added++;
            }
            await db.SaveChangesAsync(ct);
        }

        log.LogInformation("  {Label}: {Added} new", label, added);
        return added;
    }

    private async Task<Dictionary<int, string>> LoadGenresAsync(HttpClient http, string apiKey, string type, CancellationToken ct)
    {
        var dto = await GetAsync<GenreList>(http, $"{Base}/genre/{type}/list?api_key={apiKey}&language=en-US", ct);
        return dto?.Genres.ToDictionary(g => g.Id, g => g.Name) ?? [];
    }

    // 429 → honour Retry-After; transient 5xx → a few retries with growing delays.
    private async Task<T?> GetAsync<T>(HttpClient http, string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return default;

            var retry = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (retry && attempt < 5)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                log.LogWarning("TMDB returned {Status}, sleeping {Wait}s", (int)response.StatusCode, wait.TotalSeconds);
                await Task.Delay(wait, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed record GenreList([property: JsonPropertyName("genres")] List<Genre> Genres);
    private sealed record Genre([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("name")] string Name);

    private sealed record DiscoverPage(
        [property: JsonPropertyName("total_pages")] int TotalPages,
        [property: JsonPropertyName("results")] List<DiscoverItem> Results);

    private sealed record DiscoverItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("original_title")] string? OriginalTitle,
        [property: JsonPropertyName("original_name")] string? OriginalName,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("genre_ids")] List<int>? GenreIds,
        [property: JsonPropertyName("vote_average")] double VoteAverage,
        [property: JsonPropertyName("vote_count")] int VoteCount);
}
