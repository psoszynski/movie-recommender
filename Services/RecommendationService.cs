using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using MovieRecommender.Data;
using MovieRecommender.Models;

namespace MovieRecommender.Services;

/// <summary>Optional relational filters that are combined with the vector similarity.</summary>
public sealed record SearchFilters(string? Genre, double? MinRating, int? MinRatings, int? YearFrom, int? YearTo, int Top = 10, string? MediaType = null);

public sealed record Hit(int MovieId, string Title, int? Year, string Genres, double? AvgRating, int RatingCount, double Distance, string? OverviewSnippet, string MediaType);

/// <summary>
/// The payoff: vector similarity *inside* ordinary LINQ queries, side by side with
/// WHERE clauses on normal columns. This is the main argument for vectors in a relational
/// database vs. a separate vector store — one query, one transaction, one engine.
///
/// EF translates
///     .OrderBy(m => EF.Functions.VectorDistance("cosine", m.Embedding, vec))
/// to
///     ORDER BY VECTOR_DISTANCE('cosine', [m].[Embedding], @vec)
/// Cosine distance: 0 = identical direction, 1 = orthogonal, 2 = opposite. Lower = closer.
/// </summary>
public sealed class RecommendationService(MovieDbContext db, EmbedService embedService)
{
    /// <summary>Free-text semantic search: "feel-good 90s comedy about friendship".</summary>
    public async Task<List<Hit>> SearchAsync(string query, SearchFilters f, CancellationToken ct)
    {
        var vec = await embedService.EmbedQueryAsync(query, ct);
        return await RankAsync(vec, f, excludeMovieIds: null, ct);
    }

    /// <summary>Item-to-item: "movies like The Matrix". Uses the stored vector, no model call.</summary>
    public async Task<(Movie? Anchor, List<Hit> Hits)> SimilarAsync(string title, SearchFilters f, CancellationToken ct)
    {
        // NOTE: we project instead of loading the Movie entity. With EF Core 11.0.0 a nullable
        // SqlVector<float>? property comes back as null when materialised *as part of an entity*,
        // while the same column read through a projection is populated correctly. Projections
        // are the reliable path for reading vectors back; see also EmbedService.
        var anchor = await db.Movies
            .Where(m => m.Embedding != null && EF.Functions.Like(m.Title, $"%{title}%"))
            .OrderByDescending(m => m.RatingCount)      // most-rated match wins on ambiguity
            .Select(m => new { m.MovieId, m.Title, m.Year, m.Genres, Embedding = m.Embedding!.Value })
            .FirstOrDefaultAsync(ct);

        if (anchor is null) return (null, []);

        var hits = await RankAsync(anchor.Embedding, f, excludeMovieIds: [anchor.MovieId], ct);
        return (new Movie { MovieId = anchor.MovieId, Title = anchor.Title, Year = anchor.Year, Genres = anchor.Genres }, hits);
    }

    /// <summary>
    /// Content-based user recommendations. We build a "taste vector" by averaging the
    /// embeddings of movies the user rated ≥ 4 stars, weighted by rating, then look for
    /// nearest unseen movies. No training, no collaborative filtering — just geometry.
    /// </summary>
    public async Task<(int LikedCount, List<Hit> Hits)> ForUserAsync(int userId, SearchFilters f, CancellationToken ct)
    {
        var liked = await db.Ratings
            .Where(r => r.UserId == userId && r.Stars >= 4 && r.Movie.Embedding != null)
            .Select(r => new { r.Stars, r.Movie.Embedding })
            .ToListAsync(ct);

        if (liked.Count == 0) return (0, []);

        // Weighted centroid in C#. ~hundreds of 768-d vectors — trivial.
        var centroid = new float[EmbeddingConfig.Dimensions];
        float totalWeight = 0;
        foreach (var l in liked)
        {
            var w = l.Stars - 3.5f;             // 4★ → 0.5, 5★ → 1.5
            var v = l.Embedding!.Value.Memory.Span;
            for (var i = 0; i < centroid.Length; i++) centroid[i] += w * v[i];
            totalWeight += w;
        }
        for (var i = 0; i < centroid.Length; i++) centroid[i] /= totalWeight;

        var seen = await db.Ratings.Where(r => r.UserId == userId).Select(r => r.MovieId).ToListAsync(ct);

        var hits = await RankAsync(new SqlVector<float>(centroid), f, excludeMovieIds: seen, ct);
        return (liked.Count, hits);
    }

    private async Task<List<Hit>> RankAsync(SqlVector<float> vec, SearchFilters f, IReadOnlyCollection<int>? excludeMovieIds, CancellationToken ct)
    {
        IQueryable<Movie> q = db.Movies.Where(m => m.Embedding != null);

        // Each filter becomes a plain WHERE clause in the same SQL statement as the vector sort.
        if (f.MediaType is not null) q = q.Where(m => m.MediaType == f.MediaType);
        if (f.Genre is not null) q = q.Where(m => EF.Functions.Like(m.Genres, $"%{f.Genre}%"));
        if (f.MinRating is not null) q = q.Where(m => m.AvgRating >= f.MinRating);
        if (f.MinRatings is not null) q = q.Where(m => m.RatingCount >= f.MinRatings);
        if (f.YearFrom is not null) q = q.Where(m => m.Year >= f.YearFrom);
        if (f.YearTo is not null) q = q.Where(m => m.Year <= f.YearTo);
        if (excludeMovieIds is { Count: > 0 }) q = q.Where(m => !excludeMovieIds.Contains(m.MovieId));

        // Project to an anonymous type first: EF can translate member access on anonymous
        // projections, but not on a record constructed via `new Hit(...)` inside OrderBy.
        var rows = await q
            .Select(m => new
            {
                m.MovieId, m.Title, m.Year, m.Genres, m.AvgRating, m.RatingCount, m.MediaType,
                Distance = EF.Functions.VectorDistance("cosine", m.Embedding!.Value, vec),
                // Truncate server-side (translates to SUBSTRING) so we don't ship full overviews.
                OverviewSnippet = m.Overview == null || m.Overview == "" ? null : m.Overview.Substring(0, 80),
            })
            .OrderBy(x => x.Distance)
            .Take(f.Top)
            .ToListAsync(ct);

        return rows.Select(x => new Hit(x.MovieId, x.Title, x.Year, x.Genres, x.AvgRating, x.RatingCount, x.Distance, x.OverviewSnippet, x.MediaType)).ToList();
    }
}
