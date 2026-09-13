using Microsoft.EntityFrameworkCore;
using SqlFlow.Assistant;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The retrieval contract POWERAI.md Section 6 rests on: a newly typed question finds the stored question that
/// means the same thing even when they share few words, and each match arrives with the SQL that already
/// answers it plus a similarity score the caller can gate on. Embeddings here come from a deterministic
/// stand-in rather than a real provider, so what is under test is the ranking, resolution, and model-skew
/// behavior rather than any vendor's semantics.
/// </summary>
[Trait("Category", "Integration")]
public sealed class QuestionSearchTests
{
    /// <summary>
    /// Embeds on a fixed vocabulary: each text becomes a vector of term frequencies over
    /// <see cref="Vocabulary"/>. That makes similarity depend on shared MEANING as encoded by these terms
    /// rather than on exact string equality, which is what the test needs to prove ranking works, while staying
    /// deterministic and offline.
    /// </summary>
    private sealed class VocabularyEmbeddingProvider(string model = "test-embed-v1") : IEmbeddingProvider
    {
        private static readonly string[][] Vocabulary =
        [
            ["revenue", "sales", "selling", "sell", "turnover"],
            ["region", "regional", "area", "territory"],
            ["product", "category", "categories"],
            ["customer", "customers", "client"],
            ["time", "month", "monthly", "year", "trend"],
        ];

        public string Model => model;

        public int Dimensions => Vocabulary.Length;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            IReadOnlyList<float[]> vectors = [.. texts.Select(Embed)];
            return Task.FromResult(vectors);
        }

        private static float[] Embed(string text)
        {
            var words = text.ToLowerInvariant().Split(
                [' ', '?', ',', '.', '\'', '"', '-'], StringSplitOptions.RemoveEmptyEntries);
            var vector = new float[Vocabulary.Length];
            for (var i = 0; i < Vocabulary.Length; i++)
            {
                vector[i] = words.Count(w => Vocabulary[i].Contains(w, StringComparer.Ordinal));
            }
            return vector;
        }
    }

    [SkippableFact]
    public async Task FindSimilar_RanksAParaphraseFirst_AndCarriesTheSqlThatAnswersIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|search_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";
        var embedder = new VocabularyEmbeddingProvider();

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey, embedder,
            [
                ("What is our revenue by region?", "SELECT Region, SUM(Revenue) FROM Sales GROUP BY Region",
                    "[Dw].[arc].[Sales]", "Revenue by Region"),
                ("Which product category sells the most?", "SELECT Category, SUM(Amount) FROM Sales GROUP BY Category",
                    "[Dw].[arc].[Sales]", "Top Categories"),
                ("How many customers do we have?", "SELECT COUNT(*) FROM Customer", "[Dw].[arc].[Customer]",
                    "Customer Count"),
            ]);

            // Shares almost no words with the stored question ("turnover"/"territory" versus
            // "revenue"/"region"), which is exactly the paraphrase gap keyword matching cannot close.
            var matches = await QuestionSearch.FindSimilarAsync(
                db, "what was our turnover per territory", topK: 2, embedder, repoId, CancellationToken.None);

            Assert.Equal(2, matches.Count);
            var best = matches[0];
            Assert.Equal("What is our revenue by region?", best.Question);
            Assert.True(best.Similarity > matches[1].Similarity,
                $"the paraphrase ({best.Similarity}) should outrank the runner-up ({matches[1].Similarity})");

            // A match is only useful if it carries what answers the question, not just the question text.
            Assert.Equal("SELECT Region, SUM(Revenue) FROM Sales GROUP BY Region", best.Sql);
            Assert.Equal(["[Dw].[arc].[Sales]"], best.ObjectKeys);
            Assert.Equal(QuestionSearch.PowerBiProvenance, best.Provenance);
            Assert.Equal(subscriberKey, best.SubscriberKey);
            Assert.Equal("Revenue by Region", best.VisualTitle);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    [SkippableFact]
    public async Task FindSimilar_SkipsRowsEmbeddedByADifferentModel()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|skew_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";
        var oldModel = new VocabularyEmbeddingProvider("test-embed-v0");

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey, oldModel,
            [
                ("What is our revenue by region?", "SELECT 1", "[Dw].[arc].[Sales]", "Revenue by Region"),
            ]);

            // Searching with a NEWER model must not rank against vectors from the old one: two models do not
            // share a coordinate space, so comparing across them yields confident nonsense. The rows are
            // re-embedded by the next sync, so an empty result here is correct and temporary.
            var current = new VocabularyEmbeddingProvider("test-embed-v1");
            var matches = await QuestionSearch.FindSimilarAsync(
                db, "what was our turnover per territory", topK: 3, current, repoId, CancellationToken.None);

            Assert.Empty(matches);

            // The same search with the model those rows were actually embedded by still finds them.
            var sameModel = await QuestionSearch.FindSimilarAsync(
                db, "what was our turnover per territory", topK: 3, oldModel, repoId, CancellationToken.None);
            Assert.Single(sameModel);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    [SkippableFact]
    public async Task FindSimilar_WithNothingEmbedded_ReturnsEmptyWithoutEmbeddingTheQuestion()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var db = CatalogDatabase.Create(cs);

        // An estate that has never run the embedding step costs no embeddings call to search: there is nothing
        // to rank against, so the question is never sent anywhere.
        var matches = await QuestionSearch.FindSimilarAsync(
            db, "what was our turnover per territory", topK: 3,
            new ThrowingEmbeddingProvider(), Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(matches);
    }

    /// <summary>A provider that fails if called, so a test can prove a search short-circuited before embedding.</summary>
    private sealed class ThrowingEmbeddingProvider : IEmbeddingProvider
    {
        public string Model => "test-embed-v1";

        public int Dimensions => 5;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
            => throw new InvalidOperationException("the question must not be embedded when nothing can match it");
    }

    private static async Task SeedAsync(
        CatalogDbContext db, Guid repoId, string subscriberKey, string pageKey, IEmbeddingProvider embedder,
        IReadOnlyList<(string Question, string Sql, string ObjectKey, string Title)> rows)
    {
        db.SubscriberReportPages.Add(new CatalogSubscriberReportPage
        {
            RepoId = repoId, SubscriberKey = subscriberKey, PageKey = pageKey,
            ReportFile = "report.pbix", Ordinal = 1, DisplayName = "Page 1",
        });

        var vectors = await embedder.EmbedAsync(rows.Select(r => r.Question).ToArray(), CancellationToken.None);

        for (var i = 0; i < rows.Count; i++)
        {
            var (question, sql, objectKey, title) = rows[i];
            var visualKey = $"{pageKey}#{i + 1}";
            var queryName = $"report.pbix / Page 1 / {title}";

            db.SubscriberReportVisuals.Add(new CatalogSubscriberReportVisual
            {
                RepoId = repoId, PageKey = pageKey, VisualKey = visualKey, Ordinal = i + 1,
                VisualType = "barChart", Title = title, QueryName = queryName,
                ContentHash = SubscriberReportVisualHash.Compute(title, "barChart", []),
            });
            db.SubscriberQueries.Add(new CatalogSubscriberQuery
            {
                RepoId = repoId, SubscriberKey = subscriberKey, Ordinal = i + 1, Name = queryName,
                ServerRef = "dw", Sql = sql, ObjectKeys = objectKey,
            });
            db.SubscriberReportVisualQuestions.Add(new CatalogSubscriberReportVisualQuestion
            {
                RepoId = repoId, VisualKey = visualKey, Ordinal = 1, Question = question,
                Embedding = EmbeddingMath.ToBytes(vectors[i]),
                EmbeddingModel = embedder.Model,
                EmbeddedAtUtc = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(CatalogDbContext db, Guid repoId)
    {
        await db.SubscriberReportVisualQuestions.Where(q => q.RepoId == repoId).ExecuteDeleteAsync();
        await db.SubscriberQueries.Where(q => q.RepoId == repoId).ExecuteDeleteAsync();
        await db.SubscriberReportVisuals.Where(v => v.RepoId == repoId).ExecuteDeleteAsync();
        await db.SubscriberReportPages.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
    }
}
