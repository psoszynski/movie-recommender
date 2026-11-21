# MovieRecommender — semantic movie search with EF Core 11, SQL Server 2025 vectors and Ollama

A learning project that turns the public **MovieLens** dataset into a semantic search +
recommendation engine, entirely on your Mac:

- **.NET 11 / EF Core 11** code-first model with a native `vector(768)` column
- **SQL Server 2025** (Docker) — `VECTOR_DISTANCE` combined with ordinary `WHERE` filters
- **Ollama + nomic-embed-text** — free, local embeddings via `Microsoft.Extensions.AI`
- Plot overviews from **TMDB** (optional API key) or **Wikipedia** (no key)

See [ARCHITECTURE.md](ARCHITECTURE.md) for diagrams and the "why" behind each piece, or open the
**[interactive explainer](https://psoszynski.github.io/movie-recommender/)** — a web page built
from this project's real embeddings that walks through embedding, vector distance, exact scan vs.
DiskANN indexing (see [§7](#7-interactive-explainer-page-docs)).

---

## 0. Prerequisites

| Tool | Check | Install |
|---|---|---|
| .NET 11 SDK | `dotnet --version` → 11.x | https://dot.net |
| Docker Desktop | `docker --version` | https://docker.com |
| Ollama | `ollama --version` | `brew install ollama` |
| dotnet-ef (optional, for new migrations) | `dotnet ef --version` | `dotnet tool install -g dotnet-ef` |

## 1. Start SQL Server 2025

The project shares one container with `../vector-search-ef`. Start it from either folder:

```sh
docker compose up -d
docker compose ps          # wait for "healthy" (~30 s on first start)
```

| Setting | Value |
|---|---|
| Server | `localhost,1433` |
| Login / password | `sa` / `YourStrong!Passw0rd` |
| Database | `MovieRec` (created by migrations) |
| Edition | Developer (free, not for production) |

> **Apple Silicon**: the image is x64-only. Enable *Docker Desktop → Settings → General →
> "Use Rosetta for x86_64/amd64 emulation"*. `platform: linux/amd64` is pinned in compose.

Handy: `docker compose stop` / `start` keep data; `docker compose down -v` wipes everything.

## 2. Start Ollama and pull the embedding model

```sh
ollama serve &                 # or start the Ollama menu-bar app
ollama pull nomic-embed-text   # ~270 MB, 768-dimensional embeddings
```

Verify:

```sh
curl -s localhost:11434/api/embed -d '{"model":"nomic-embed-text","input":"hello"}' | head -c 200
```

## 3. (Optional) TMDB API key — better plot summaries

1. Create a free account at https://www.themoviedb.org, then *Settings → API → Request an API key*.
2. `export TMDB_API_KEY=your_key_here`

Without a key the `enrich` step falls back to Wikipedia page summaries (~60 % hit rate,
occasional wrong article, and Wikipedia rate-limits aggressively — expect the full pass to take
an hour or more with retries). Both work; **TMDB is strongly recommended** for the full dataset.
The pipeline is resumable, so you can also just let `enrich` run in chunks whenever convenient.

## 4. Build the database

```sh
dotnet run -- sync
```

`sync` is idempotent and resumable — Ctrl‑C anytime, run again, it continues. It performs:

| Step | Command | What happens | Time (M‑series Mac) |
|---|---|---|---|
| 1 | `migrate` | EF migrations create `MovieRec` with `Movies`, `Ratings`, `Tags` | 2 s |
| 2 | `import` | Downloads `ml-latest-small.zip` (1 MB), loads 9.7k movies, 100k ratings, 3.7k tags, computes rating aggregates | 10 s |
| 3 | `enrich` | Fetches plot overviews (4 parallel HTTP requests) | TMDB: ~5 min · Wikipedia: 1 h+ |
| 4 | `embed` | Embeds `title + year + genres + tags + overview` with Ollama in batches of 32 | ~2.5 min |

`embed` is also *self-healing*: if an overview arrives after a movie was already embedded, the
stored `EmbeddingText` no longer matches and the movie is re-embedded on the next run.

Check progress at any time with `dotnet run -- stats`.

## 5. Query

All query commands accept the same filters:
`--genre X  --min-rating 3.5  --min-ratings 20  --after 1990  --before 2010  --top 10`

```sh
# Free-text semantic search
dotnet run -- search "a lonely robot finds love" --after 2000
dotnet run -- search "heist crew plans one last job" --min-ratings 50

# Movies similar to a given title (uses the stored vector, no model call)
dotnet run -- similar "The Matrix" --genre Sci-Fi
dotnet run -- similar "Toy Story" --after 2005

# Content-based recommendations for a MovieLens user (1–610)
dotnet run -- for-user 42 --min-ratings 30
```

Output columns: cosine **dist**ance (lower = closer), average **rating**, number of **votes**, title.

## 6. Day-to-day commands

```sh
dotnet run -- stats                  # pipeline progress
dotnet run -- embed --force          # recompute all vectors (after changing the embedding text/model)
dotnet run -- enrich --limit 100     # try 100 more movies only
dotnet run -- reset                  # drop & recreate the database
```

### Changing the schema

```sh
# edit Models/Entities.cs or Data/MovieDbContext.cs, then:
dotnet ef migrations add <DescriptiveName>
dotnet run -- migrate
```

### Looking at the data directly

```sh
docker exec -it vector-demo-sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'YourStrong!Passw0rd' -C -d MovieRec \
  -Q "SELECT TOP 3 Title, LEFT(EmbeddingText,120) FROM Movies WHERE Embedding IS NOT NULL"
```

Try raw T-SQL vector search:

```sql
DECLARE @q vector(768) = (SELECT TOP 1 Embedding FROM Movies WHERE Title = 'The Matrix');
SELECT TOP 5 Title, VECTOR_DISTANCE('cosine', Embedding, @q) AS dist
FROM Movies WHERE Embedding IS NOT NULL ORDER BY dist;
```

## 7. Interactive explainer page (`docs/`)

`docs/` is a static site (three.js + SVG, no build step) that explains how semantic indexing works
using the vectors from *your* database:

1. **Embeddings** — the exact text composed per movie, its 768 floats, and Ollama vs. Azure AI Foundry code.
2. **Vector space** — 3,650 movies projected to 3-D with PCA; hover/click to see true 768-d neighbours, or fire real queries.
3. **Distance** — drag two vectors to see cosine / euclidean / dot change; real cosine matrix between stored vectors.
4. **Database** — `vector(768)` schema, EF Core LINQ, and an animated `VECTOR_DISTANCE` full scan.
5. **Indexing** — DiskANN greedy graph search animation + `CREATE VECTOR INDEX` / `VECTOR_SEARCH` (not yet used in this repo).

Regenerate the data file after re-embedding (needs Ollama for the sample queries):

```sh
dotnet run -- export-viz                 # writes docs/data/movies.json (~1 MB, movies with ≥5 ratings)
dotnet run -- export-viz --min-ratings 1 # include every movie
python3 -m http.server 8765 -d docs      # preview at http://localhost:8765/
```

To publish on GitHub Pages: repo **Settings → Pages → Deploy from a branch → `main` / `/docs`**.

## Configuration (environment variables)

| Variable | Default | Purpose |
|---|---|---|
| `MOVIEREC_CONNECTION` | docker-compose values | SQL Server connection string |
| `OLLAMA_URL` | `http://localhost:11434` | Ollama endpoint |
| `TMDB_API_KEY` | *(unset)* | Enables TMDB overviews |
| `MOVIEREC_DATA_DIR` | `bin/.../data` | Where the MovieLens zip is cached |

## Experiments to try

- Set `Microsoft.EntityFrameworkCore` logging to `Information` in `Program.cs` and read the generated `VECTOR_DISTANCE` SQL.
- Remove the `search_document:` / `search_query:` prefixes in `EmbedService` and re-embed — compare result quality.
- Change `BuildDocumentText` (e.g. drop genres, or add top-rated user tags only) and `embed --force`.
- Swap `nomic-embed-text` for `mxbai-embed-large` (1024 dims): update `EmbeddingConfig`, add a migration, `embed --force`.
- Compare `euclidean` vs `cosine` in `RecommendationService.RankAsync`.
- Try the `for-user` centroid with different weighting (`Stars - 2.5`, or only 5★ movies).
- Add hybrid search: SQL Server full-text index on `Overview` + reciprocal-rank fusion with the vector ranking.

## Data licence

MovieLens data © GroupLens Research, University of Minnesota — free for research and
education, see https://grouplens.org/datasets/movielens/. Plot summaries © TMDB or
Wikipedia contributors (CC BY-SA).
