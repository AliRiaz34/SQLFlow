using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The retrieval contract POWERAI.md Section 6 rests on: a newly typed question finds the stored question that
/// means the same thing even when the two are worded differently, and each match arrives with the SQL that
/// answers it plus a score the caller can gate on. Expansion is supplied by the test rather than by Anthropic,
/// so what is under test is the matching, scoring, and resolution rather than any model's vocabulary.
/// </summary>
[Trait("Category", "Integration")]
public sealed class QuestionSearchTests
{
    [SkippableFact]
    public async Task ExpandedTerms_FindAQuestionWordedDifferently_AndCarryTheSqlThatAnswersIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|search_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("What is our revenue by region?", "SELECT Region, SUM(Revenue) FROM Sales GROUP BY Region",
                    "[Dw].[arc].[Sales]", "Revenue by Region"),
                ("Which product category sells the most?", "SELECT Category, SUM(Amount) FROM Sales GROUP BY Category",
                    "[Dw].[arc].[Sales]", "Top Categories"),
                ("How many customers do we have?", "SELECT COUNT(*) FROM Customer", "[Dw].[arc].[Customer]",
                    "Customer Count"),
            ]);

            // The typed question shares no content word with the stored one ("turnover"/"territory" versus
            // "revenue"/"region"): the expansion is what closes that gap, which is the whole point of the
            // LLM step. Supplied here directly so the test does not depend on a model's wording.
            var result = await QuestionSearch.FindSimilarAsync(
                db, "what was our turnover per territory", topK: 3,
                ["turnover", "revenue", "sales", "territory", "region", "product"], repoId,
                CancellationToken.None);

            // Two questions carry a searched term ("revenue"+"region", and "product"); the customer-count one
            // carries none and is absent entirely rather than ranked last with a zero score.
            Assert.Equal(2, result.Matches.Count);
            var best = result.Matches[0];
            Assert.Equal("What is our revenue by region?", best.Question);
            Assert.True(best.Score > result.Matches[1].Score,
                $"the intended match ({best.Score}) should outrank the runner-up ({result.Matches[1].Score})");
            Assert.DoesNotContain(result.Matches, m => m.Question.Contains("customers", StringComparison.Ordinal));

            // A match must carry what ANSWERS the question, not just the question text.
            Assert.Equal("SELECT Region, SUM(Revenue) FROM Sales GROUP BY Region", best.Sql);
            Assert.Equal(["[Dw].[arc].[Sales]"], best.ObjectKeys);
            Assert.Equal(QuestionSearch.PowerBiProvenance, best.Provenance);
            Assert.Equal(subscriberKey, best.SubscriberKey);
            Assert.Equal("Revenue by Region", best.VisualTitle);

            // The caller can explain WHY it matched, not only how strongly.
            Assert.Contains("revenue", best.MatchedTerms);
            Assert.Contains("region", best.MatchedTerms);
            Assert.DoesNotContain("turnover", best.MatchedTerms);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    /// <summary>
    /// Pins a real limitation rather than hiding it: without SQL Server's full-text feature installed the
    /// search falls back to plain word matching, which has NO stemming, so "sales" does not reach a question
    /// worded "sells". Where full-text IS installed the engine supplies that inflection matching. The
    /// expansion step is what covers the gap in the meantime, by returning inflected forms among its terms.
    /// This test asserts the fallback's honest behavior; it is not asserting that the gap is desirable.
    /// </summary>
    [SkippableFact]
    public async Task WithoutFullText_InflectionsDoNotMatch_WhichIsWhatExpansionIsFor()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        Skip.If(await HasFullTextAsync(cs), "SQL Server has full-text installed; its engine stems for us.");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|stem_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("Which product category sells the most?", "SELECT 1", "[Dw].[arc].[Sales]", "Top Categories"),
            ]);

            var unstemmed = await QuestionSearch.FindSimilarAsync(
                db, "sales", topK: 3, ["sales"], repoId, CancellationToken.None);
            Assert.Empty(unstemmed.Matches);

            // An expansion that includes the inflected form finds it, which is how this is meant to work.
            var expanded = await QuestionSearch.FindSimilarAsync(
                db, "sales", topK: 3, ["sales", "sells"], repoId, CancellationToken.None);
            Assert.Single(expanded.Matches);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    private static async Task<bool> HasFullTextAsync(string connectionString)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('IsFullTextInstalled') AS int)";
        return (int?)await command.ExecuteScalarAsync() == 1;
    }

    [SkippableFact]
    public async Task AStopWordQuestion_MatchesNothing_RatherThanEverything()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|stop_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("What is our revenue by region?", "SELECT 1", "[Dw].[arc].[Sales]", "Revenue by Region"),
            ]);

            // Every word here is a stop word. Were they kept, they would match nearly every stored question
            // and rank noise above real vocabulary matches, so the correct answer is no match at all.
            var result = await QuestionSearch.FindSimilarAsync(
                db, "what is our the", topK: 3, expander: null, repoId, CancellationToken.None);

            Assert.Empty(result.Matches);
            Assert.Empty(result.SearchedTerms);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    [SkippableFact]
    public async Task WithNoExpander_TheTypedWordsStillSearch()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|noexp_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("What is our revenue by region?", "SELECT 1", "[Dw].[arc].[Sales]", "Revenue by Region"),
            ]);

            // Expansion off (or Anthropic unreachable): the search must still run on the typed words, so a
            // model outage degrades retrieval rather than breaking it.
            var result = await QuestionSearch.FindSimilarAsync(
                db, "revenue by region", topK: 3, expander: null, repoId, CancellationToken.None);

            var match = Assert.Single(result.Matches);
            Assert.Equal("What is our revenue by region?", match.Question);
            Assert.Equal(2, match.Score);
            Assert.Equal(["revenue", "region"], result.SearchedTerms);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    [SkippableFact]
    public async Task AWordIsMatchedWhole_SoSaleDoesNotMatchWholesale()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|word_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("How much wholesale volume did we move?", "SELECT 1", "[Dw].[arc].[Wholesale]", "Wholesale"),
            ]);

            // "sale" occurs INSIDE "wholesale". Scoring on substrings would call that a match and hand a
            // caller an unrelated dashboard, so the term must match on word boundaries.
            var result = await QuestionSearch.FindSimilarAsync(
                db, "sale", topK: 3, expander: null, repoId, CancellationToken.None);

            Assert.Empty(result.Matches);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    [SkippableFact]
    public async Task ATermWithAnApostrophe_IsMatchedAsTextRatherThanBreakingTheQuery()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|quote_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("What is each customer's lifetime value?", "SELECT 1", "[Dw].[arc].[Customer]", "CLV"),
            ]);

            // A term carrying an apostrophe, and one carrying full-text operators, must be treated as text.
            // Were terms pasted into a predicate string these would break the query or change its meaning.
            var result = await QuestionSearch.FindSimilarAsync(
                db, "customer's lifetime value OR NEAR(\"x\")", topK: 3, expander: null, repoId,
                CancellationToken.None);

            var match = Assert.Single(result.Matches);
            Assert.Equal("What is each customer's lifetime value?", match.Question);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    private static async Task SeedAsync(
        CatalogDbContext db, Guid repoId, string subscriberKey, string pageKey,
        IReadOnlyList<(string Question, string Sql, string ObjectKey, string Title)> rows)
    {
        db.SubscriberReportPages.Add(new CatalogSubscriberReportPage
        {
            RepoId = repoId, SubscriberKey = subscriberKey, PageKey = pageKey,
            ReportFile = "report.pbix", Ordinal = 1, DisplayName = "Page 1",
        });

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
