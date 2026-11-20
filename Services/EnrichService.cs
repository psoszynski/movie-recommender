using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieRecommender.Data;

namespace MovieRecommender.Services;

/// <summary>
/// Step 2: fetch plot overviews for movies that don't have one yet.
///
/// Resumability trick: Overview has three states —
///   NULL  → never tried            (this command will process it)
///   ""    → tried, nothing found   (skipped on later runs)
///   text  → done
/// So you can Ctrl-C at any time and simply run `enrich` again.
///
/// Concurrency: a SemaphoreSlim caps in-flight HTTP requests so we stay polite with the
/// API while still being much faster than sequential calls. Results are flushed to the DB
/// in batches of 100 so a crash loses at most 100 lookups.
/// </summary>
public sealed class EnrichService(MovieDbContext db, IOverviewProvider provider, ILogger<EnrichService> log)
{
    public async Task RunAsync(int? limit, int concurrency, CancellationToken ct)
    {
        var query = db.Movies.Where(m => m.Overview == null).OrderBy(m => m.MovieId);
        if (limit is int l) query = (IOrderedQueryable<Models.Movie>)query.Take(l);

        var pending = await query.ToListAsync(ct);
        log.LogInformation("{Count} movies need an overview (provider: {Provider})", pending.Count, provider.Name);

        var gate = new SemaphoreSlim(concurrency);
        var done = 0;
        var found = 0;

        foreach (var chunk in pending.Chunk(100))
        {
            // Fan out one chunk concurrently, then persist it in a single SaveChanges.
            await Task.WhenAll(chunk.Select(async movie =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var text = await provider.GetOverviewAsync(movie, ct);
                    movie.Overview = text ?? "";
                    if (text is not null) Interlocked.Increment(ref found);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Leave Overview NULL so the next run retries this movie.
                    log.LogWarning("{Title}: {Error}", movie.Title, ex.Message);
                }
                finally
                {
                    gate.Release();
                }
            }));

            await db.SaveChangesAsync(ct);
            done += chunk.Length;
            log.LogInformation("  {Done}/{Total} processed, {Found} overviews found", done, pending.Count, found);
        }

        log.LogInformation("Enrich complete.");
    }
}
