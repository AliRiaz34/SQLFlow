using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Assistant;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The incremental-regeneration contract POWERAI.md's business-question field depends on: a visual whose
/// <c>ContentHash</c> is unchanged from the pre-sync snapshot carries its questions forward with NO call to
/// the generator (proven here with a generator pointed at an invalid endpoint, so any real call would fail the
/// test), while a new or changed visual is sent to it. A generation failure degrades to a warning and never
/// touches the visual/field rows a sync already wrote.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SubscriberQuestionEnrichmentTests
{
    /// <summary>An Anthropic key that authenticates with nobody: any call through it fails fast, so a test
    /// asserting "the generator was never invoked" can prove it by using this and expecting no exception.</summary>
    private static QuestionGenerator UncallableGenerator()
        => new(new AnthropicOptions { ApiKey = "sk-ant-invalid-test-key", Model = "claude-sonnet-5" },
            NullLogger<QuestionGenerator>.Instance);

    [SkippableFact]
    public async Task UnchangedContentHash_CarriesQuestionsForward_WithNoGeneratorCall()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|enrich_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";
        var visualKey = $"{pageKey}#1";
        var now = DateTime.UtcNow;

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            var hash = SubscriberReportVisualHash.Compute(
                "Sales by Region", "barChart",
                [new SubscriberReportVisualHash.FieldContent("Category", "Sales", "Region", false)]);

            db.Subscribers.Add(new CatalogSubscriber
            {
                RepoId = repoId, Name = $"Report {suffix}", Type = "PowerBI", ObjectKey = subscriberKey,
                File = "subscribers.yaml", FirstSeenUtc = now, LastSeenUtc = now,
            });
            db.SubscriberReportPages.Add(new CatalogSubscriberReportPage
            {
                RepoId = repoId, SubscriberKey = subscriberKey, PageKey = pageKey, ReportFile = "report.pbix",
                Ordinal = 1, Name = "ReportSection1", DisplayName = "Page 1",
            });
            db.SubscriberReportVisuals.Add(new CatalogSubscriberReportVisual
            {
                RepoId = repoId, PageKey = pageKey, VisualKey = visualKey, Ordinal = 1, VisualType = "barChart",
                Title = "Sales by Region", QueryName = "report.pbix / Page 1 / Sales by Region", ContentHash = hash,
            });
            db.SubscriberReportFields.Add(new CatalogSubscriberReportField
            {
                RepoId = repoId, VisualKey = visualKey, Role = "Category", TableName = "Sales",
                ColumnOrMeasure = "Region", IsMeasure = false,
            });
            db.SubscriberReportVisualQuestions.Add(new CatalogSubscriberReportVisualQuestion
            {
                RepoId = repoId, VisualKey = visualKey, Ordinal = 1, Question = "What are sales by region?",
                Embedding = EmbeddingMath.ToBytes([0.5f, 0.25f]),
                EmbeddingModel = "text-embedding-3-small",
                EmbeddedAtUtc = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();

            // Snapshot before the "sync": same shape SubscriberQuestionEnrichment.SnapshotAsync would read
            // immediately before CatalogSync deletes and reinserts these rows.
            var snapshot = await SubscriberQuestionEnrichment.SnapshotAsync(db, repoId, CancellationToken.None);
            Assert.True(snapshot.ContainsKey(visualKey));
            Assert.Equal(hash, snapshot[visualKey].ContentHash);
            Assert.Equal(["What are sales by region?"], snapshot[visualKey].Questions.Select(q => q.Question));

            // Simulate the sync's wholesale delete+reinsert writing back the SAME content (same hash).
            await db.SubscriberReportVisualQuestions.Where(q => q.RepoId == repoId).ExecuteDeleteAsync();

            using var generator = UncallableGenerator();
            var warnings = await SubscriberQuestionEnrichment.EnrichAsync(
                db, repoId, snapshot, generator, CancellationToken.None);

            Assert.Empty(warnings);
            var carried = await db.SubscriberReportVisualQuestions.AsNoTracking()
                .Where(q => q.VisualKey == visualKey).OrderBy(q => q.Ordinal).ToListAsync();
            var question = Assert.Single(carried);
            Assert.Equal("What are sales by region?", question.Question);

            // The embedding carries forward with the text it belongs to: identical text re-embeds to the
            // identical vector, so paying for that call again would be waste.
            Assert.Equal("text-embedding-3-small", question.EmbeddingModel);
            Assert.Equal([0.5f, 0.25f], EmbeddingMath.FromBytes(question.Embedding!));
        }
        finally
        {
            await db.SubscriberReportVisualQuestions.Where(q => q.RepoId == repoId).ExecuteDeleteAsync();
            await db.SubscriberReportFields.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.SubscriberReportVisuals.Where(v => v.RepoId == repoId).ExecuteDeleteAsync();
            await db.SubscriberReportPages.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Subscribers.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task GenerationFailure_DegradesToWarning_AndLeavesVisualFieldRowsIntact()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|enrichfail_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";
        var visualKey = $"{pageKey}#1";
        var now = DateTime.UtcNow;

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            db.Subscribers.Add(new CatalogSubscriber
            {
                RepoId = repoId, Name = $"Report {suffix}", Type = "PowerBI", ObjectKey = subscriberKey,
                File = "subscribers.yaml", FirstSeenUtc = now, LastSeenUtc = now,
            });
            db.SubscriberReportPages.Add(new CatalogSubscriberReportPage
            {
                RepoId = repoId, SubscriberKey = subscriberKey, PageKey = pageKey, ReportFile = "report.pbix",
                Ordinal = 1, Name = "ReportSection1", DisplayName = "Page 1",
            });
            // A brand-new visual: no prior snapshot entry, so it must go to the (uncallable) generator.
            db.SubscriberReportVisuals.Add(new CatalogSubscriberReportVisual
            {
                RepoId = repoId, PageKey = pageKey, VisualKey = visualKey, Ordinal = 1, VisualType = "barChart",
                Title = "New Visual", QueryName = "report.pbix / Page 1 / New Visual",
                ContentHash = SubscriberReportVisualHash.Compute("New Visual", "barChart", []),
            });
            db.SubscriberReportFields.Add(new CatalogSubscriberReportField
            {
                RepoId = repoId, VisualKey = visualKey, Role = "Category", TableName = "Sales",
                ColumnOrMeasure = "Region", IsMeasure = false,
            });
            await db.SaveChangesAsync();

            using var generator = UncallableGenerator();
            var emptySnapshot = new Dictionary<string, SubscriberQuestionEnrichment.VisualSnapshot>();
            var warnings = await SubscriberQuestionEnrichment.EnrichAsync(
                db, repoId, emptySnapshot, generator, CancellationToken.None);

            Assert.Single(warnings);
            Assert.Contains("no business questions could be generated", warnings[0]);

            // The visual and field rows the (simulated) sync wrote are untouched by the generation failure.
            var visualStillThere = await db.SubscriberReportVisuals.AsNoTracking()
                .AnyAsync(v => v.VisualKey == visualKey);
            var fieldStillThere = await db.SubscriberReportFields.AsNoTracking()
                .AnyAsync(f => f.VisualKey == visualKey);
            Assert.True(visualStillThere);
            Assert.True(fieldStillThere);
        }
        finally
        {
            await db.SubscriberReportVisualQuestions.Where(q => q.RepoId == repoId).ExecuteDeleteAsync();
            await db.SubscriberReportFields.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.SubscriberReportVisuals.Where(v => v.RepoId == repoId).ExecuteDeleteAsync();
            await db.SubscriberReportPages.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Subscribers.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>A provider that embeds without a network call, counting what it was asked to embed so a test
    /// can prove a row was skipped rather than merely re-embedded to the same value.</summary>
    private sealed class FakeEmbeddingProvider(string model) : IEmbeddingProvider
    {
        public string Model => model;

        public int Dimensions => 2;

        public List<string> Embedded { get; } = [];

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Embedded.AddRange(texts);
            // Deterministic and text-dependent, so a vector identifies the text it came from.
            IReadOnlyList<float[]> vectors = [.. texts.Select(t => new[] { t.Length / 100f, 0.5f })];
            return Task.FromResult(vectors);
        }
    }

    [SkippableFact]
    public async Task EmbedQuestions_FillsMissingVectors_AndReEmbedsOnlyOnAModelChange()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var visualKey = $"subscriber|embed_{suffix}#report.pbix#1#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            db.SubscriberReportVisualQuestions.AddRange(
                new CatalogSubscriberReportVisualQuestion
                {
                    RepoId = repoId, VisualKey = visualKey, Ordinal = 1, Question = "What are sales by region?",
                },
                new CatalogSubscriberReportVisualQuestion
                {
                    RepoId = repoId, VisualKey = visualKey, Ordinal = 2, Question = "Which region sells most?",
                });
            await db.SaveChangesAsync();

            // First pass: neither row has a vector, so both are embedded.
            var first = new FakeEmbeddingProvider("model-a");
            var warnings = await SubscriberQuestionEnrichment.EmbedQuestionsAsync(
                db, repoId, first, TimeProvider.System, CancellationToken.None);

            Assert.Empty(warnings);
            Assert.Equal(2, first.Embedded.Count);
            var stored = await db.SubscriberReportVisualQuestions.AsNoTracking()
                .Where(q => q.RepoId == repoId).OrderBy(q => q.Ordinal).ToListAsync();
            Assert.All(stored, q =>
            {
                Assert.Equal("model-a", q.EmbeddingModel);
                Assert.NotNull(q.EmbeddedAtUtc);
                Assert.Equal(2, EmbeddingMath.FromBytes(q.Embedding!).Length);
            });

            // Second pass, same model: every row already carries a current vector, so nothing is re-embedded.
            var second = new FakeEmbeddingProvider("model-a");
            await SubscriberQuestionEnrichment.EmbedQuestionsAsync(
                db, repoId, second, TimeProvider.System, CancellationToken.None);
            Assert.Empty(second.Embedded);

            // Third pass, different model: vectors from two models are not comparable, so both rows are redone.
            var upgraded = new FakeEmbeddingProvider("model-b");
            await SubscriberQuestionEnrichment.EmbedQuestionsAsync(
                db, repoId, upgraded, TimeProvider.System, CancellationToken.None);

            Assert.Equal(2, upgraded.Embedded.Count);
            var reEmbedded = await db.SubscriberReportVisualQuestions.AsNoTracking()
                .Where(q => q.RepoId == repoId).ToListAsync();
            Assert.All(reEmbedded, q => Assert.Equal("model-b", q.EmbeddingModel));
        }
        finally
        {
            await db.SubscriberReportVisualQuestions.Where(q => q.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>A provider whose every call fails, standing in for an embeddings outage or a bad credential.</summary>
    private sealed class FailingEmbeddingProvider : IEmbeddingProvider
    {
        public string Model => "model-down";

        public int Dimensions => 2;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
            => throw new HttpRequestException("embeddings endpoint unreachable");
    }

    [SkippableFact]
    public async Task EmbedQuestions_WhenTheProviderFails_WarnsAndLeavesTheQuestionIntact()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var visualKey = $"subscriber|embedfail_{suffix}#report.pbix#1#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            db.SubscriberReportVisualQuestions.Add(new CatalogSubscriberReportVisualQuestion
            {
                RepoId = repoId, VisualKey = visualKey, Ordinal = 1, Question = "What are sales by region?",
            });
            await db.SaveChangesAsync();

            var warnings = await SubscriberQuestionEnrichment.EmbedQuestionsAsync(
                db, repoId, new FailingEmbeddingProvider(), TimeProvider.System, CancellationToken.None);

            // An embeddings outage is a warning, never a thrown exception: the question rows a sync already
            // wrote must survive it, and the row is simply retried on the next sync.
            var warning = Assert.Single(warnings);
            Assert.Contains("could not be embedded", warning);

            var question = Assert.Single(await db.SubscriberReportVisualQuestions.AsNoTracking()
                .Where(q => q.RepoId == repoId).ToListAsync());
            Assert.Equal("What are sales by region?", question.Question);
            Assert.Null(question.Embedding);
        }
        finally
        {
            await db.SubscriberReportVisualQuestions.Where(q => q.RepoId == repoId).ExecuteDeleteAsync();
        }
    }
}
