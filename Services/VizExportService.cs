using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieRecommender.Data;

namespace MovieRecommender.Services;

/// <summary>
/// Exports a compact JSON snapshot of the vector space for the interactive explainer page in
/// <c>docs/</c>. Nothing here is needed by the recommender itself; it exists so the web page can
/// show *real* data instead of a toy illustration.
///
/// What it does:
///   1. Loads every embedded title (movies and series) with at least <c>minRatings</c> ratings
///      (0 = everything; raise it to shrink the file and favour titles people recognise).
///   2. Runs PCA (768 → 3) so the browser can plot movies as points in 3-D. PCA is a linear
///      projection, so distances are only approximately preserved — the page says so.
///   3. For each movie computes its 5 nearest neighbours by cosine in the *full* 768-d space,
///      so hovering a point can show "true" neighbours vs. what the 3-D picture suggests.
///   4. Embeds a handful of example queries with the same model and projects them with the same
///      PCA basis, so the page can animate "query lands here, nearest movies are these".
/// </summary>
public sealed class VizExportService(MovieDbContext db, EmbedService embed, ILogger<VizExportService> log)
{
    private static readonly string[] SampleQueries =
    [
        "a lonely robot left on an abandoned Earth falls in love",
        "hackers discover reality is a computer simulation",
        "a heist crew pulls one last job and it goes wrong",
        "coming-of-age summer romance in a small town",
        "space opera with a rebellion fighting an evil empire",
        "serial killer hunted by an FBI profiler",
    ];

    public async Task RunAsync(string outputPath, int minRatings, CancellationToken ct)
    {
        var rows = await db.Movies
            .Where(m => m.Embedding != null && m.RatingCount >= minRatings)
            .OrderBy(m => m.MovieId)
            .Select(m => new
            {
                m.MovieId, m.Title, m.Year, m.Genres, m.AvgRating, m.RatingCount, m.MediaType, m.Source,
                Overview = m.Overview == null ? "" : m.Overview.Substring(0, 140),
                m.EmbeddingText,
                Embedding = m.Embedding!.Value,
            })
            .ToListAsync(ct);

        log.LogInformation("Exporting {Count} movies with ≥{Min} ratings", rows.Count, minRatings);

        var n = rows.Count;
        var d = Models.EmbeddingConfig.Dimensions;
        var x = new float[n][];
        for (var i = 0; i < n; i++) x[i] = rows[i].Embedding.Memory.ToArray();

        // ---- PCA --------------------------------------------------------------------------
        var mean = new double[d];
        foreach (var v in x) for (var j = 0; j < d; j++) mean[j] += v[j];
        for (var j = 0; j < d; j++) mean[j] /= n;

        var centred = new double[n][];
        for (var i = 0; i < n; i++)
        {
            centred[i] = new double[d];
            for (var j = 0; j < d; j++) centred[i][j] = x[i][j] - mean[j];
        }

        var basis = PrincipalComponents(centred, 3, ct);
        var coords = new double[n][];
        for (var i = 0; i < n; i++) coords[i] = Project(centred[i], basis);
        var scale = NormaliseToUnitCube(coords);

        // ---- Exact neighbours in full space -----------------------------------------------
        log.LogInformation("Computing exact top-5 neighbours ({N}² cosine comparisons)…", n);
        var norms = x.Select(v => Math.Sqrt(v.Sum(f => (double)f * f))).ToArray();
        var neighbours = new int[n][];
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var best = new PriorityQueue<int, double>(); // min-heap on similarity; keep 5 largest
            for (var k = 0; k < n; k++)
            {
                if (k == i) continue;
                var sim = Dot(x[i], x[k]) / (norms[i] * norms[k]);
                if (best.Count < 5) best.Enqueue(k, sim);
                else if (best.TryPeek(out _, out var worst) && sim > worst)
                {
                    best.Dequeue();
                    best.Enqueue(k, sim);
                }
            }
            neighbours[i] = best.UnorderedItems.OrderByDescending(p => p.Priority).Select(p => p.Element).ToArray();
        });

        // ---- Example queries --------------------------------------------------------------
        var queries = new List<object>();
        foreach (var q in SampleQueries)
        {
            var vec = (await embed.EmbedQueryAsync(q, ct)).Memory.ToArray();
            var qn = Math.Sqrt(vec.Sum(f => (double)f * f));
            var top = Enumerable.Range(0, n)
                .Select(i => (i, dist: 1 - Dot(vec, x[i]) / (qn * norms[i])))
                .OrderBy(t => t.dist)
                .Take(8)
                .Select(t => new { i = t.i, dist = Math.Round(t.dist, 4) })
                .ToArray();

            // A short query vector sits systematically away from document vectors (the "modality
            // gap"), and a 3-D PCA exaggerates that offset. So the marker is drawn at the centre of
            // its nearest matches: PCA is linear, so this is the projection of their mean vector.
            var p = new double[3];
            foreach (var t in top) for (var k = 0; k < 3; k++) p[k] += coords[t.i][k] / top.Length;

            queries.Add(new { text = q, pos = p.Select(v => Math.Round(v, 4)).ToArray(), top });
        }

        // ---- Featured movies with full vectors (for the "what is an embedding" section) -----
        string[] featuredTitles = ["Toy Story", "The Matrix", "WALL·E", "Toy Story 2"];
        var featured = featuredTitles
            .Select(t => rows.FindIndex(r => r.Title == t))
            .Where(i => i >= 0)
            .Select(i => new { i, txt = rows[i].EmbeddingText, v = x[i].Select(f => Math.Round(f, 4)).ToArray() })
            .ToArray();

        // ---- Write ------------------------------------------------------------------------
        var payload = new
        {
            model = Models.EmbeddingConfig.ModelName,
            dimensions = d,
            exportedAt = DateTime.UtcNow,
            count = n,
            explainedVariance = basis.ExplainedVariance.Select(v => Math.Round(v, 4)).ToArray(),
            featured,
            movies = rows.Select((r, i) => new
            {
                id = r.MovieId,
                t = r.Title,
                y = r.Year,
                g = r.Genres,
                r = r.AvgRating is double a ? Math.Round(a, 2) : (double?)null,
                n = r.RatingCount,
                ty = r.MediaType,
                s = r.Source,
                o = r.Overview,
                p = coords[i].Select(v => Math.Round(v, 4)).ToArray(),
                nn = neighbours[i],
            }),
            queries,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await using var fs = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(fs, payload, new JsonSerializerOptions { WriteIndented = false }, ct);
        log.LogInformation("Wrote {Path} ({Size:N0} bytes). PCA explains {Var:P1} of variance in 3 dims.",
            outputPath, fs.Length, basis.ExplainedVariance.Sum());
    }

    // Power iteration with deflation. Fine for 3 components on a few thousand × 768 matrix;
    // a real pipeline would use a proper SVD, but this keeps the demo dependency-free.
    private static (double[][] Vectors, double[] ExplainedVariance) PrincipalComponents(double[][] data, int k, CancellationToken ct)
    {
        var n = data.Length;
        var d = data[0].Length;
        var total = data.Sum(row => row.Sum(v => v * v)) / n;
        var vectors = new double[k][];
        var variance = new double[k];
        var residual = data.Select(r => (double[])r.Clone()).ToArray();
        var rng = new Random(42);

        for (var c = 0; c < k; c++)
        {
            var v = new double[d];
            for (var j = 0; j < d; j++) v[j] = rng.NextDouble() - 0.5;
            Normalise(v);

            for (var iter = 0; iter < 60; iter++)
            {
                ct.ThrowIfCancellationRequested();
                // v ← Xᵀ (X v), normalised — converges to the top eigenvector of XᵀX.
                var xv = new double[n];
                for (var i = 0; i < n; i++) xv[i] = Dot(residual[i], v);
                var next = new double[d];
                for (var i = 0; i < n; i++)
                    for (var j = 0; j < d; j++) next[j] += residual[i][j] * xv[i];
                Normalise(next);
                v = next;
            }

            var proj = residual.Select(r => Dot(r, v)).ToArray();
            variance[c] = proj.Sum(p => p * p) / n / total;
            vectors[c] = v;

            // Deflate: remove this component so the next iteration finds the next one.
            for (var i = 0; i < n; i++)
                for (var j = 0; j < d; j++) residual[i][j] -= proj[i] * v[j];
        }

        return (vectors, variance);
    }

    private static double[] Project(double[] row, (double[][] Vectors, double[] _) basis)
        => basis.Vectors.Select(v => Dot(row, v)).ToArray();

    // Fit all points into roughly [-1, 1]³ while keeping aspect ratio (one shared factor).
    private static (double Factor, double[] Offset) NormaliseToUnitCube(double[][] coords)
    {
        var min = new double[3]; var max = new double[3];
        Array.Fill(min, double.MaxValue); Array.Fill(max, double.MinValue);
        foreach (var c in coords)
            for (var k = 0; k < 3; k++) { min[k] = Math.Min(min[k], c[k]); max[k] = Math.Max(max[k], c[k]); }

        var span = Enumerable.Range(0, 3).Max(k => max[k] - min[k]);
        var factor = 2.0 / span;
        var offset = new double[3];
        for (var k = 0; k < 3; k++) offset[k] = (min[k] + max[k]) / 2 * factor;

        foreach (var c in coords)
            for (var k = 0; k < 3; k++) c[k] = c[k] * factor - offset[k];

        return (factor, offset);
    }

    private static double Dot(double[] a, double[] b) { double s = 0; for (var i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }
    private static double Dot(float[] a, float[] b) { double s = 0; for (var i = 0; i < a.Length; i++) s += (double)a[i] * b[i]; return s; }
    private static void Normalise(double[] v) { var l = Math.Sqrt(v.Sum(x => x * x)); for (var i = 0; i < v.Length; i++) v[i] /= l; }
}
