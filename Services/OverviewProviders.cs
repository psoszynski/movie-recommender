using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MovieRecommender.Models;

namespace MovieRecommender.Services;

/// <summary>
/// Abstraction over "where do plot summaries come from". Two implementations:
///   * <see cref="TmdbOverviewProvider"/>  – best quality, needs a free API key.
///   * <see cref="WikipediaOverviewProvider"/> – no key, noisier (title-based lookup).
/// The enrich command picks one at startup based on configuration.
/// </summary>
public interface IOverviewProvider
{
    string Name { get; }
    /// <summary>Returns null when nothing usable was found (caller marks the movie as "tried").</summary>
    Task<string?> GetOverviewAsync(Movie movie, CancellationToken ct);
}

/// <summary>
/// TMDB (themoviedb.org) v3 API. MovieLens ships the tmdbId for almost every movie, so this
/// is an exact lookup: GET /movie/{id}. Free tier allows ~50 requests/second.
/// </summary>
public sealed class TmdbOverviewProvider(HttpClient http, string apiKey, ILogger<TmdbOverviewProvider> log)
    : IOverviewProvider
{
    public string Name => "TMDB";

    public async Task<string?> GetOverviewAsync(Movie movie, CancellationToken ct)
    {
        if (movie.TmdbId is null) return null;

        using var response = await http.GetAsync(
            $"https://api.themoviedb.org/3/movie/{movie.TmdbId}?api_key={apiKey}&language=en-US", ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // TMDB tells us how long to back off. Honour it, then retry once.
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
            log.LogWarning("Rate limited by TMDB, sleeping {Wait}s", wait.TotalSeconds);
            await Task.Delay(wait, ct);
            return await GetOverviewAsync(movie, ct);
        }

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<TmdbMovie>(ct);
        return string.IsNullOrWhiteSpace(dto?.Overview) ? null : dto.Overview.Trim();
    }

    private sealed record TmdbMovie([property: JsonPropertyName("overview")] string? Overview);
}

/// <summary>
/// Fallback: Wikipedia REST "page summary" endpoint, no authentication required.
/// We try "Title (year film)" first, then "Title (film)", then plain "Title", and accept the
/// result only if it looks like a film article. Expect ~70–80% hit rate and occasional
/// mismatches — a good reminder of why clean source data matters for embeddings.
/// </summary>
public sealed class WikipediaOverviewProvider(HttpClient http, ILogger<WikipediaOverviewProvider> log)
    : IOverviewProvider
{
    public string Name => "Wikipedia";

    public async Task<string?> GetOverviewAsync(Movie movie, CancellationToken ct)
    {
        string[] candidates = movie.Year is int y
            ? [$"{movie.Title} ({y} film)", $"{movie.Title} (film)", movie.Title]
            : [$"{movie.Title} (film)", movie.Title];

        foreach (var title in candidates)
        {
            var url = "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(title.Replace(' ', '_'));
            using var response = await GetWithBackoffAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            // Anything else (5xx, 403…) is a transient failure: throw so the caller leaves
            // Overview NULL and the movie is retried next time, instead of marking it "not found".
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<WikiSummary>(ct);
            if (dto is null || dto.Type == "disambiguation") continue;

            var text = dto.Extract?.Trim();
            // Heuristic: require the word "film" so "Heat" doesn't return the thermodynamics article.
            if (!string.IsNullOrWhiteSpace(text) && text.Contains("film", StringComparison.OrdinalIgnoreCase))
                return text;
        }
        return null;
    }

    /// <summary>
    /// Wikipedia answers 429 with a Retry-After header when a client is too eager.
    /// Sleep for that long and retry a few times before giving up on this request.
    /// (Lesson learned the hard way: the first version of this demo got itself throttled.)
    /// </summary>
    private async Task<HttpResponseMessage> GetWithBackoffAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await http.GetAsync(url, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= 3)
                return response;

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10 * attempt);
            response.Dispose();
            log.LogWarning("Rate limited by Wikipedia, sleeping {Wait}s (attempt {Attempt})", wait.TotalSeconds, attempt);
            await Task.Delay(wait, ct);
        }
    }

    private sealed record WikiSummary(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("extract")] string? Extract);
}
