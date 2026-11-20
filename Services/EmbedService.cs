using System.Text;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MovieRecommender.Data;
using MovieRecommender.Models;

namespace MovieRecommender.Services;

/// <summary>
/// Step 3: turn each movie into a 768-d vector with nomic-embed-text running in Ollama.
///
/// Key ideas:
///  * IEmbeddingGenerator (Microsoft.Extensions.AI) is the provider-neutral interface.
///    OllamaSharp implements it; swapping to Azure OpenAI is a one-line DI change.
///  * nomic-embed-text is an *asymmetric* model: documents should be prefixed with
///    "search_document: " and queries with "search_query: ". Skipping the prefixes still
///    works but measurably hurts retrieval quality.
///  * We embed a composed text (title, year, genres, overview, popular tags) rather than
///    the overview alone — the metadata carries real signal for recommendations.
///  * Resumable + self-healing: rows with no vector, or whose EmbeddingText no longer matches
///    the freshly built document text, are (re)processed. --force wipes everything first.
/// </summary>
public sealed class EmbedService(
    MovieDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    ILogger<EmbedService> log)
{
    public const string DocumentPrefix = "search_document: ";
    public const string QueryPrefix = "search_query: ";

    public async Task RunAsync(bool force, int batchSize, CancellationToken ct)
    {
        if (force)
        {
            log.LogInformation("--force: clearing all existing embeddings");
            await db.Movies.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Embedding, (SqlVector<float>?)null)
                .SetProperty(m => m.EmbeddingText, (string?)null)
                .SetProperty(m => m.EmbeddingModel, (string?)null), ct);
        }

        // Pull the top-5 tags per movie in one query instead of N+1 lazy loads.
        var topTags = (await db.Tags
                .GroupBy(t => new { t.MovieId, Text = t.Text.ToLower() })
                .Select(g => new { g.Key.MovieId, g.Key.Text, Count = g.Count() })
                .ToListAsync(ct))
            .GroupBy(x => x.MovieId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Count).Take(5).Select(x => x.Text).ToArray());

        // Stale detection: recompute the document text for every movie and re-embed those
        // whose stored EmbeddingText differs (e.g. an overview arrived after the first embed
        // pass, or BuildDocumentText changed). Cheap: 10k string builds in memory.
        //
        // We compare EmbeddingText, not Embedding, on purpose: EmbeddingText and Embedding are
        // always written together, and EF Core 11.0.0 materialises a nullable SqlVector<float>?
        // entity property as null even when the column has data (projections work fine).
        var all = await db.Movies.OrderBy(m => m.MovieId).ToListAsync(ct);
        var pending = all
            .Select(m => (Movie: m, Text: BuildDocumentText(m, topTags.GetValueOrDefault(m.MovieId, []))))
            .Where(x => x.Movie.EmbeddingText != x.Text)
            .ToList();

        log.LogInformation("{Count} movies need embeddings (model: {Model})", pending.Count, EmbeddingConfig.ModelName);

        var done = 0;
        foreach (var batch in pending.Chunk(batchSize))
        {
            var texts = batch.Select(x => x.Text).ToList();

            // One HTTP call to Ollama per batch; it returns one vector per input, in order.
            var embeddings = await embedder.GenerateAsync(texts, cancellationToken: ct);

            foreach (var ((movie, text), embedding) in batch.Zip(embeddings))
            {
                if (embedding.Vector.Length != EmbeddingConfig.Dimensions)
                    throw new InvalidOperationException(
                        $"Model returned {embedding.Vector.Length} dims, column is vector({EmbeddingConfig.Dimensions}).");

                movie.EmbeddingText = text;
                movie.EmbeddingModel = EmbeddingConfig.ModelName;
                movie.Embedding = new SqlVector<float>(embedding.Vector);
            }

            await db.SaveChangesAsync(ct);
            done += batch.Length;
            log.LogInformation("  {Done}/{Total} embedded", done, pending.Count);
        }

        log.LogInformation("Embed complete.");
    }

    /// <summary>
    /// The text the model sees. Keep it human-readable: you can inspect Movie.EmbeddingText
    /// in the DB to understand why a movie matched a query.
    /// </summary>
    public static string BuildDocumentText(Movie m, string[] tags)
    {
        var sb = new StringBuilder(DocumentPrefix);
        sb.Append(m.Title);
        if (m.Year is int y) sb.Append(" (").Append(y).Append(')');
        if (m.Genres.Length > 0) sb.Append(". Genres: ").Append(m.Genres.Replace('|', ','));
        if (tags.Length > 0) sb.Append(". Tags: ").Append(string.Join(", ", tags));
        if (!string.IsNullOrWhiteSpace(m.Overview)) sb.Append(". ").Append(m.Overview);
        return sb.ToString();
    }

    /// <summary>Embeds a free-text user query with the matching "search_query:" prefix.</summary>
    public async Task<SqlVector<float>> EmbedQueryAsync(string query, CancellationToken ct)
    {
        var vector = await embedder.GenerateVectorAsync(QueryPrefix + query, cancellationToken: ct);
        return new SqlVector<float>(vector);
    }
}
