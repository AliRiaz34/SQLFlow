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
            });
            await db.SaveChangesAsync();

            // Snapshot before the "sync": same shape SubscriberQuestionEnrichment.SnapshotAsync would read
            // immediately before CatalogSync deletes and reinserts these rows.
            var snapshot = await SubscriberQuestionEnrichment.SnapshotAsync(db, repoId, CancellationToken.None);
            Assert.True(snapshot.ContainsKey(visualKey));
            Assert.Equal(hash, snapshot[visualKey].ContentHash);
            Assert.Equal(["What are sales by region?"], snapshot[visualKey].Questions);

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
}
