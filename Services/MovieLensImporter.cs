using System.IO.Compression;
using System.Text.RegularExpressions;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieRecommender.Data;
using MovieRecommender.Models;

namespace MovieRecommender.Services;

/// <summary>
/// Step 1 of the pipeline: download the MovieLens "ml-latest-small" dataset and load it
/// into SQL Server. Idempotent: if Movies already has rows it does nothing.
///
/// Dataset: https://grouplens.org/datasets/movielens/latest/  (≈1 MB zip, CC-licensed for
/// research/education). Files inside the zip:
///   movies.csv   movieId,title,genres
///   ratings.csv  userId,movieId,rating,timestamp
///   tags.csv     userId,movieId,tag,timestamp
///   links.csv    movieId,imdbId,tmdbId
/// </summary>
public sealed partial class MovieLensImporter(
    MovieDbContext db,
    IHttpClientFactory httpFactory,
    ILogger<MovieLensImporter> log)
{
    private const string ZipUrl = "https://files.grouplens.org/datasets/movielens/ml-latest-small.zip";

    public async Task RunAsync(CancellationToken ct)
    {
        if (await db.Movies.AnyAsync(ct))
        {
            log.LogInformation("Movies table already populated — skipping import. Use `reset` to start over.");
            return;
        }

        var dir = await EnsureDatasetAsync(ct);

        // ---- movies + links -------------------------------------------------------------
        var links = ReadCsv(Path.Combine(dir, "links.csv"),
                r => new LinkRow(r.Parse<int>("movieId"), r.GetStringOrNull("imdbId"), r.GetString("tmdbId") is { Length: > 0 } t ? int.Parse(t) : null))
            .ToDictionary(l => l.MovieId);
        var movies = ReadCsv(Path.Combine(dir, "movies.csv"),
                r => new MovieRow(r.Parse<int>("movieId"), r.GetString("title"), r.GetString("genres")))
            .Select(r =>
            {
                var (title, year) = SplitTitleYear(r.Title);
                return new Movie
                {
                    MovieId = r.MovieId,
                    Title = title,
                    Year = year,
                    Genres = r.Genres == "(no genres listed)" ? "" : r.Genres,
                    TmdbId = links.TryGetValue(r.MovieId, out var l) ? l.TmdbId : null,
                };
            })
            .ToList();

        // ---- ratings (100k rows) --------------------------------------------------------
        // We also fold the aggregates into Movie here, in memory, instead of running a
        // GROUP BY later. One pass over the data, no extra round-trips.
        var ratings = new List<Rating>(110_000);
        var agg = new Dictionary<int, (double Sum, int Count)>();
        foreach (var r in ReadCsv(Path.Combine(dir, "ratings.csv"),
                     r => new RatingRow(r.Parse<int>("userId"), r.Parse<int>("movieId"), r.Parse<float>("rating"), r.Parse<long>("timestamp"))))
        {
            ratings.Add(new Rating
            {
                UserId = r.UserId,
                MovieId = r.MovieId,
                Stars = r.Rating,
                RatedAt = DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime,
            });
            var a = agg.GetValueOrDefault(r.MovieId);
            agg[r.MovieId] = (a.Sum + r.Rating, a.Count + 1);
        }
        foreach (var m in movies)
        {
            if (agg.TryGetValue(m.MovieId, out var a))
            {
                m.AvgRating = Math.Round(a.Sum / a.Count, 2);
                m.RatingCount = a.Count;
            }
        }

        var tags = ReadCsv(Path.Combine(dir, "tags.csv"),
                r => new TagRow(r.Parse<int>("userId"), r.Parse<int>("movieId"), r.GetString("tag"), r.Parse<long>("timestamp")))
            .Where(t => !string.IsNullOrWhiteSpace(t.Tag))
            .Select(t => new Tag { UserId = t.UserId, MovieId = t.MovieId, Text = t.Tag.Trim() })
            .ToList();

        // ---- persist --------------------------------------------------------------------
        // EF Core is not a bulk-loading tool, but 110k small rows is fine if we:
        //   * turn off change tracking (we never read these entities back here),
        //   * save in batches so the SQL batch size stays reasonable.
        // For millions of rows you would reach for SqlBulkCopy or EFCore.BulkExtensions.
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        log.LogInformation("Inserting {Movies} movies...", movies.Count);
        await InsertInBatchesAsync(movies, 2_000, ct);

        log.LogInformation("Inserting {Ratings} ratings...", ratings.Count);
        await InsertInBatchesAsync(ratings, 5_000, ct);

        log.LogInformation("Inserting {Tags} tags...", tags.Count);
        await InsertInBatchesAsync(tags, 5_000, ct);

        log.LogInformation("Import complete.");
    }

    private async Task InsertInBatchesAsync<T>(List<T> rows, int batchSize, CancellationToken ct) where T : class
    {
        for (var i = 0; i < rows.Count; i += batchSize)
        {
            db.AddRange(rows.Skip(i).Take(batchSize));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();   // drop tracked entities so memory stays flat
        }
    }

    /// <summary>Downloads and unzips the dataset once; subsequent runs reuse the files.</summary>
    private async Task<string> EnsureDatasetAsync(CancellationToken ct)
    {
        var root = AppSettings.DataDir;
        var extracted = Path.Combine(root, "ml-latest-small");
        if (File.Exists(Path.Combine(extracted, "movies.csv")))
            return extracted;

        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "ml-latest-small.zip");

        log.LogInformation("Downloading {Url}...", ZipUrl);
        using var http = httpFactory.CreateClient();
        await using (var src = await http.GetStreamAsync(ZipUrl, ct))
        await using (var dst = File.Create(zipPath))
            await src.CopyToAsync(dst, ct);

        ZipFile.ExtractToDirectory(zipPath, root, overwriteFiles: true);
        return extracted;
    }

    /// <summary>
    /// CsvHelper 34 exposes each row as typed accessors on the reader (by header name).
    /// We map rows to small records with a lambda; ToList() materialises everything before
    /// the reader is disposed.
    /// </summary>
    private static List<T> ReadCsv<T>(string path, Func<CsvReader, T> map)
    {
        using var reader = CsvReader.FromFile(path);
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(map(reader));
        return rows;
    }

    /// <summary>"Toy Story (1995)" → ("Toy Story", 1995). Also fixes "Matrix, The" → "The Matrix".</summary>
    internal static (string Title, int? Year) SplitTitleYear(string raw)
    {
        var m = TitleYearRegex().Match(raw.Trim());
        var title = m.Success ? m.Groups["title"].Value.Trim() : raw.Trim();
        int? year = m.Success ? int.Parse(m.Groups["year"].Value) : null;

        var art = ArticleSuffixRegex().Match(title);
        if (art.Success)
            title = $"{art.Groups["article"].Value} {art.Groups["title"].Value}";

        return (title, year);
    }

    [GeneratedRegex(@"^(?<title>.+?)\s*\((?<year>\d{4})\)\s*$")]
    private static partial Regex TitleYearRegex();

    [GeneratedRegex(@"^(?<title>.+),\s*(?<article>The|A|An|Le|La|Les|Der|Die|Das|El|Il)$")]
    private static partial Regex ArticleSuffixRegex();

    // Plain DTOs for the four CSV files.
    private sealed record MovieRow(int MovieId, string Title, string Genres);
    private sealed record RatingRow(int UserId, int MovieId, float Rating, long Timestamp);
    private sealed record TagRow(int UserId, int MovieId, string Tag, long Timestamp);
    private sealed record LinkRow(int MovieId, string? ImdbId, int? TmdbId);
}
