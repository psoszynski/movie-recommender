using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieRecommender.Data;
using MovieRecommender.Models;
using MovieRecommender.Services;
using OllamaSharp;
using Spectre.Console;

// ----------------------------------------------------------------------------------------
// Composition root. We use the Generic Host only for its DI container + logging; there is
// no long-running service here. Each CLI invocation = one command = one process.
// ----------------------------------------------------------------------------------------
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning); // set to Information to see the SQL
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

builder.Services.AddDbContext<MovieDbContext>(o => o.UseSqlServer(AppSettings.ConnectionString));
builder.Services.AddHttpClient();

// OllamaApiClient implements IEmbeddingGenerator<string, Embedding<float>> directly.
// To switch providers, replace this single registration (e.g. with an Azure OpenAI client).
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    _ => new OllamaApiClient(new Uri(AppSettings.OllamaUrl), EmbeddingConfig.ModelName));

// Pick the overview source at startup: TMDB if a key is configured, Wikipedia otherwise.
builder.Services.AddSingleton<IOverviewProvider>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieRecommenderDemo/1.0 (personal learning project; contact via github.com/psoszynski)");
    return AppSettings.TmdbApiKey is { Length: > 0 } key
        ? new TmdbOverviewProvider(http, key, sp.GetRequiredService<ILogger<TmdbOverviewProvider>>())
        : new WikipediaOverviewProvider(http, sp.GetRequiredService<ILogger<WikipediaOverviewProvider>>());
});

builder.Services.AddScoped<MovieLensImporter>();
builder.Services.AddScoped<TmdbImporter>();
builder.Services.AddScoped<EnrichService>();
builder.Services.AddScoped<EmbedService>();
builder.Services.AddScoped<RecommendationService>();
builder.Services.AddScoped<VizExportService>();

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var sp = scope.ServiceProvider;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

// ----------------------------------------------------------------------------------------
// Tiny hand-rolled CLI. args[0] is the command; the rest are positional words or --flags.
// ----------------------------------------------------------------------------------------
var (command, positional, flags) = ParseArgs(args);

try
{
    switch (command)
    {
        case "migrate":
            await sp.GetRequiredService<MovieDbContext>().Database.MigrateAsync(ct);
            Console.WriteLine("Database is up to date.");
            break;

        case "import":
            await sp.GetRequiredService<MovieDbContext>().Database.MigrateAsync(ct);
            await sp.GetRequiredService<MovieLensImporter>().RunAsync(ct);
            break;

        case "import-tmdb":
        {
            if (AppSettings.TmdbApiKey is not { Length: > 0 } tmdbKey) return Usage("import-tmdb needs the TMDB_API_KEY environment variable");
            var moviesOnly = flags.ContainsKey("movies-only");
            var tvOnly = flags.ContainsKey("tv-only");
            await sp.GetRequiredService<MovieDbContext>().Database.MigrateAsync(ct);
            await sp.GetRequiredService<TmdbImporter>().RunAsync(tmdbKey, new TmdbImportOptions(
                MovieFromYear: IntFlag("from-year") ?? 2023,
                MinMovieVotes: IntFlag("min-votes") ?? 100,
                MinTvVotes: IntFlag("tv-min-votes") ?? 100,
                MaxPagesPerSlice: IntFlag("max-pages") ?? 500,
                IncludeMovies: !tvOnly,
                IncludeTv: !moviesOnly), ct);
            break;
        }

        case "enrich":
            await sp.GetRequiredService<EnrichService>().RunAsync(
                limit: IntFlag("limit"),
                concurrency: IntFlag("concurrency") ?? 4,
                ct);
            break;

        case "embed":
            await sp.GetRequiredService<EmbedService>().RunAsync(
                force: flags.ContainsKey("force"),
                batchSize: IntFlag("batch") ?? 32,
                ct);
            break;

        case "sync":
            // Converge the database to a complete state with one command.
            await sp.GetRequiredService<MovieDbContext>().Database.MigrateAsync(ct);
            await sp.GetRequiredService<MovieLensImporter>().RunAsync(ct);
            await sp.GetRequiredService<EnrichService>().RunAsync(IntFlag("limit"), IntFlag("concurrency") ?? 4, ct);
            await sp.GetRequiredService<EmbedService>().RunAsync(false, IntFlag("batch") ?? 32, ct);
            break;

        case "search":
        {
            var query = string.Join(' ', positional);
            if (query.Length == 0) return Usage("search needs a query");
            var hits = await sp.GetRequiredService<RecommendationService>().SearchAsync(query, Filters(), ct);
            PrintHits($"Semantic search: \"{query}\"", hits);
            break;
        }

        case "similar":
        {
            var title = string.Join(' ', positional);
            if (title.Length == 0) return Usage("similar needs a movie title");
            var (anchor, hits) = await sp.GetRequiredService<RecommendationService>().SimilarAsync(title, Filters(), ct);
            if (anchor is null) { Console.WriteLine($"No embedded movie matching \"{title}\"."); break; }
            PrintHits($"Movies similar to: {anchor.Title} ({anchor.Year}) [{anchor.Genres}]", hits);
            break;
        }

        case "for-user":
        {
            if (positional.Count == 0 || !int.TryParse(positional[0], out var userId))
                return Usage("for-user needs a numeric userId (1–610 in ml-latest-small)");
            var (liked, hits) = await sp.GetRequiredService<RecommendationService>().ForUserAsync(userId, Filters(), ct);
            PrintHits($"Recommendations for user {userId} (based on {liked} movies rated ≥ 4★)", hits);
            break;
        }

        case "stats":
        {
            var db = sp.GetRequiredService<MovieDbContext>();
            Console.WriteLine($"Movies:            {await db.Movies.CountAsync(ct),8}");
            Console.WriteLine($"  with overview:   {await db.Movies.CountAsync(m => m.Overview != null && m.Overview != "", ct),8}");
            Console.WriteLine($"  overview missing:{await db.Movies.CountAsync(m => m.Overview == "", ct),8}");
            Console.WriteLine($"  not yet tried:   {await db.Movies.CountAsync(m => m.Overview == null, ct),8}");
            Console.WriteLine($"  with embedding:  {await db.Movies.CountAsync(m => m.Embedding != null, ct),8}");
            Console.WriteLine($"  from TMDB:       {await db.Movies.CountAsync(m => m.Source == "tmdb", ct),8}");
            Console.WriteLine($"  series:          {await db.Movies.CountAsync(m => m.MediaType == "tv", ct),8}");
            Console.WriteLine($"Ratings:           {await db.Ratings.CountAsync(ct),8}");
            Console.WriteLine($"Tags:              {await db.Tags.CountAsync(ct),8}");
            // Allocated size of the data files (SQL Server pre-grows files, so this can exceed the data actually used).
            var sizeMb = await db.Database
                .SqlQuery<decimal>($"SELECT CAST(SUM(size) * 8.0 / 1024 AS decimal(18,1)) AS Value FROM sys.database_files WHERE type = 0")
                .SingleAsync(ct);
            Console.WriteLine($"Database size:     {sizeMb,8:F1} MB (data files)");
            break;
        }

        case "export-viz":
            // Snapshot of the vector space for the interactive explainer in docs/.
            await sp.GetRequiredService<VizExportService>().RunAsync(
                outputPath: positional.FirstOrDefault() ?? "docs/data/movies.json",
                minRatings: IntFlag("min-ratings") ?? 0,
                ct);
            break;

        case "reset":
        {
            var db = sp.GetRequiredService<MovieDbContext>();
            Console.Write("Drop and recreate the MovieRec database? [y/N] ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() == "y")
            {
                await db.Database.EnsureDeletedAsync(ct);
                await db.Database.MigrateAsync(ct);
                Console.WriteLine("Done.");
            }
            break;
        }

        default:
            return Usage(command is null ? null : $"Unknown command '{command}'");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nCancelled. Re-run the command to resume.");
}

return 0;

// ---- helpers ----------------------------------------------------------------------------

SearchFilters Filters() => new(
    Genre: flags.GetValueOrDefault("genre"),
    MinRating: DoubleFlag("min-rating"),
    MinRatings: IntFlag("min-ratings"),
    YearFrom: IntFlag("after"),
    YearTo: IntFlag("before"),
    Top: IntFlag("top") ?? 10,
    MediaType: flags.GetValueOrDefault("type"));

int? IntFlag(string name) => flags.TryGetValue(name, out var v) && int.TryParse(v, out var i) ? i : null;
double? DoubleFlag(string name) => flags.TryGetValue(name, out var v) && double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

static (string? Command, List<string> Positional, Dictionary<string, string> Flags) ParseArgs(string[] args)
{
    var positional = new List<string>();
    var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? command = null;

    for (var i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if (a.StartsWith("--"))
        {
            var name = a[2..];
            // Flag with value ("--top 5") vs. boolean switch ("--force").
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) flags[name] = args[++i];
            else flags[name] = "true";
        }
        else if (command is null) command = a;
        else positional.Add(a);
    }
    return (command, positional, flags);
}

static void PrintHits(string heading, List<Hit> hits)
{
    AnsiConsole.WriteLine();
    if (hits.Count == 0) { AnsiConsole.MarkupLine("[yellow]No results.[/]"); return; }

    var table = new Table()
        .Title($"[bold]{Markup.Escape(heading)}[/]")
        .Border(TableBorder.Rounded)
        .AddColumn(new TableColumn("dist").RightAligned().NoWrap())
        .AddColumn(new TableColumn("★").RightAligned().NoWrap())
        .AddColumn(new TableColumn("votes").RightAligned().NoWrap())
        .AddColumn(new TableColumn("title").NoWrap())
        .AddColumn("genres")
        .AddColumn("overview");

    foreach (var h in hits)
    {
        // Colour the rating so weak/strong matches stand out at a glance.
        var ratingColor = h.AvgRating switch { >= 4.0 => "green", >= 3.0 => "yellow", null => "grey", _ => "red" };
        table.AddRow(
            $"[grey]{h.Distance:F3}[/]",
            h.AvgRating is double r ? $"[{ratingColor}]{r:F2}[/]" : "[grey]–[/]",
            h.RatingCount.ToString(),
            $"[bold]{Markup.Escape(h.Title)}[/] [grey]({h.Year})[/]{(h.MediaType == "tv" ? " [magenta]TV[/]" : "")}",
            $"[blue]{Markup.Escape(h.Genres.Replace('|', ' '))}[/]",
            h.OverviewSnippet is null ? "" : $"[grey]{Markup.Escape(h.OverviewSnippet.TrimEnd())}…[/]");
    }

    AnsiConsole.Write(table);
}

static int Usage(string? error)
{
    if (error is not null) Console.Error.WriteLine($"Error: {error}\n");
    Console.WriteLine("""
        MovieRecommender — EF Core 11 + SQL Server 2025 vectors + Ollama (nomic-embed-text)

        Pipeline:
          migrate                       apply EF migrations (creates the MovieRec database)
          import                        download MovieLens ml-latest-small and load it
          import-tmdb [--from-year 2023] [--min-votes 100] [--tv-min-votes 100] [--max-pages 500]
                      [--movies-only | --tv-only]
                                        add popular TMDB movies (from --from-year) and series (any year); needs TMDB_API_KEY
          enrich  [--limit N] [--concurrency N]
                                        fetch plot overviews (TMDB if TMDB_API_KEY set, else Wikipedia)
          embed   [--force] [--batch N] compute embeddings via Ollama for movies without one
          sync    [...same flags]       migrate + import + enrich + embed in one go
          stats                         show pipeline progress
          reset                         drop and recreate the database
          export-viz [path] [--min-ratings N]
                                        export PCA-projected vectors for the docs/ explainer page

        Queries (all accept --type movie|tv --genre X --min-rating 3.5 --min-ratings 20 --after 1990 --before 2010 --top 10):
          search   <free text>          e.g. search "a lonely robot finds love" --after 2000
          similar  <title>              e.g. similar "The Matrix" --genre Sci-Fi
          for-user <userId>             e.g. for-user 42 --min-ratings 50

        Environment:
          MOVIEREC_CONNECTION  SQL Server connection string (default: docker-compose values)
          OLLAMA_URL           default http://localhost:11434
          TMDB_API_KEY         optional, enables TMDB overviews
        """);
    return error is null ? 0 : 1;
}
