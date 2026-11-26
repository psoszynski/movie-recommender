using System.ComponentModel.DataAnnotations;
using Microsoft.Data.SqlTypes;

namespace MovieRecommender.Models;

/// <summary>
/// One MovieLens movie, enriched with a plot overview and a semantic embedding.
///
/// Design notes:
///  * MovieId is the MovieLens id (not an IDENTITY column) so that Ratings/Tags
///    imported from the CSVs can reference it directly without an id-remapping step.
///  * Overview and Embedding are nullable on purpose: the pipeline fills them in
///    separate, resumable passes (import → enrich → embed). "NULL" means "not done yet",
///    which makes each command idempotent: it only processes rows that still need work.
///  * Embedding is stored in SQL Server's native vector(768) column. 768 is the output
///    size of nomic-embed-text; see <see cref="EmbeddingConfig"/>.
/// </summary>
public class Movie
{
    public int MovieId { get; set; }

    [MaxLength(300)]
    public required string Title { get; set; }

    /// <summary>Parsed from the "(1995)" suffix in the MovieLens title. Null if missing.</summary>
    public int? Year { get; set; }

    /// <summary>Pipe-separated MovieLens genres, e.g. "Adventure|Animation|Children".</summary>
    [MaxLength(200)]
    public required string Genres { get; set; }

    /// <summary>From links.csv (or the TMDB import); used to call TMDB for the overview.</summary>
    public int? TmdbId { get; set; }

    /// <summary>"movie" or "tv". TMDB movie and TV ids live in separate namespaces.</summary>
    [MaxLength(10)]
    public string MediaType { get; set; } = "movie";

    /// <summary>"movielens" (has user ratings) or "tmdb" (ratings are TMDB vote aggregates, 0–5 scale).</summary>
    [MaxLength(20)]
    public string Source { get; set; } = "movielens";

    /// <summary>Plot summary fetched by the `enrich` command.</summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Denormalised rating aggregates. Computed once at import time so that search queries
    /// can filter/sort on them without joining 100k rating rows every time.
    /// </summary>
    public double? AvgRating { get; set; }
    public int RatingCount { get; set; }

    /// <summary>
    /// The exact text that was embedded. Stored so you can (a) inspect what the model "saw"
    /// and (b) detect when the source text changed and the vector needs recomputing.
    /// </summary>
    public string? EmbeddingText { get; set; }

    /// <summary>
    /// Which embedding model produced the vector. Vectors from different models are not
    /// comparable, so `embed --force` after a model change must wipe and recompute.
    /// </summary>
    [MaxLength(100)]
    public string? EmbeddingModel { get; set; }

    /// <summary>
    /// SqlVector&lt;float&gt; is the EF Core 10+/Microsoft.Data.SqlClient type that maps to
    /// the T-SQL `vector(n)` column. Nullable: NULL until the `embed` command runs.
    /// </summary>
    public SqlVector<float>? Embedding { get; set; }

    public List<Rating> Ratings { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
}

/// <summary>A single user's 0.5–5.0 star rating of a movie (MovieLens ratings.csv).</summary>
public class Rating
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public int MovieId { get; set; }
    public float Stars { get; set; }
    public DateTime RatedAt { get; set; }

    public Movie Movie { get; set; } = null!;
}

/// <summary>Free-text tag a user attached to a movie (MovieLens tags.csv), e.g. "mind-bending".</summary>
public class Tag
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public int MovieId { get; set; }

    [MaxLength(200)]
    public required string Text { get; set; }

    public Movie Movie { get; set; } = null!;
}

public static class EmbeddingConfig
{
    /// <summary>nomic-embed-text produces 768-dimensional vectors.</summary>
    public const int Dimensions = 768;
    public const string ModelName = "nomic-embed-text";
}
