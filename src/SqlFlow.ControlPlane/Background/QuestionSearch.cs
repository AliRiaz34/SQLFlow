using Microsoft.EntityFrameworkCore;
using SqlFlow.Assistant;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// One stored question ranked against a newly typed one: the question itself, the SQL that already answers it,
/// the warehouse objects that SQL reads, where the example came from, and how close it is. The similarity is
/// the trustworthy confidence signal (POWERAI.md Section 6: an LLM's self-rating is not, because a wrong query
/// can sound exactly as confident as a right one).
/// </summary>
/// <param name="Question">The stored question text.</param>
/// <param name="Sql">The query that answers it, empty when the visual's query could not be resolved.</param>
/// <param name="ObjectKeys">The warehouse objects <paramref name="Sql"/> reads.</param>
/// <param name="Provenance">Where the example came from: <c>powerbi</c> for a question derived from an
/// extracted report visual. The user-confirmed half of the store does not exist yet, so this is how a caller
/// tells them apart once it does, without the shape changing underneath it.</param>
/// <param name="Similarity">Cosine similarity to the asked question, in [0, 1].</param>
/// <param name="SubscriberKey">The report (subscriber) this question came from.</param>
/// <param name="VisualTitle">The title of the visual that answers it, when it has one.</param>
public sealed record QuestionMatch(
    string Question,
    string Sql,
    IReadOnlyList<string> ObjectKeys,
    string Provenance,
    double Similarity,
    string SubscriberKey,
    string? VisualTitle);

/// <summary>
/// Ranks stored questions against a newly typed one by embedding similarity (POWERAI.md Section 6). This is the
/// read half of retrieval: <see cref="SubscriberQuestionEnrichment.EmbedQuestionsAsync"/> writes the vectors at
/// sync time, and this searches them.
/// <para>
/// The distance computation runs in the application tier rather than in SQL Server, deliberately: the catalog
/// targets whatever SQL Server version a customer's estate already runs, and requiring 2025's native
/// <c>VECTOR</c> type for one feature would split the estate into "can do retrieval" and "cannot". At the scale
/// this corpus actually reaches (one row per visual across an estate's reports, plus confirmed examples, so
/// thousands rather than millions), a brute-force scan of vectors already pulled into the process is well
/// inside one request's budget and needs no index. See the design doc for when to revisit that.
/// </para>
/// </summary>
public static class QuestionSearch
{
    /// <summary>The provenance of a question derived from an extracted PowerBI report visual.</summary>
    public const string PowerBiProvenance = "powerbi";

    /// <summary>
    /// Returns the <paramref name="topK"/> stored questions closest to <paramref name="question"/>, most
    /// similar first. Rows embedded by a different model than <paramref name="embedder"/>'s are skipped rather
    /// than compared: vectors from two models do not share a coordinate space, so ranking across them would
    /// produce confident nonsense. Those rows are re-embedded by the next sync, so the omission is temporary.
    /// </summary>
    /// <param name="db">The catalog to search.</param>
    /// <param name="question">The newly typed question to rank stored examples against.</param>
    /// <param name="topK">How many matches to return, most similar first.</param>
    /// <param name="embedder">Embeds the asked question, and names the model whose stored vectors it can be
    /// compared with.</param>
    /// <param name="repoId">Restricts the search to one repo, or searches the whole estate when null. Estate-wide
    /// is the useful default: a question about revenue is worth answering from whichever repo's report first
    /// asked it.</param>
    /// <param name="ct">Cancels the search.</param>
    public static async Task<IReadOnlyList<QuestionMatch>> FindSimilarAsync(
        CatalogDbContext db, string question, int topK, IEmbeddingProvider embedder, Guid? repoId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);

        var model = embedder.Model;
        var candidates = await db.SubscriberReportVisualQuestions.AsNoTracking()
            .Where(q => q.Embedding != null && q.EmbeddingModel == model)
            .Where(q => repoId == null || q.RepoId == repoId)
            .Select(q => new { q.Question, q.Embedding, q.VisualKey })
            .ToListAsync(ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return [];
        }

        var asked = (await embedder.EmbedAsync([question], ct).ConfigureAwait(false))[0];

        // Rank first, resolve second: only the winning handful need their SQL and objects looked up, so a large
        // corpus costs one embedding call and one scan rather than a join across every stored question.
        var ranked = candidates
            .Select(c => (c.Question, c.VisualKey, Similarity: EmbeddingMath.CosineSimilarity(asked, EmbeddingMath.FromBytes(c.Embedding!))))
            .OrderByDescending(c => c.Similarity)
            .Take(topK)
            .ToList();

        var visualKeys = ranked.Select(r => r.VisualKey).Distinct().ToList();
        var visuals = await db.SubscriberReportVisuals.AsNoTracking()
            .Where(v => visualKeys.Contains(v.VisualKey))
            .Select(v => new { v.VisualKey, v.QueryName, v.Title, v.PageKey })
            .ToListAsync(ct).ConfigureAwait(false);
        var visualByKey = visuals.ToDictionary(v => v.VisualKey, StringComparer.Ordinal);

        // A visual's QueryName names the CatalogSubscriberQuery it was synthesized as, which is where the
        // rendered SQL and the objects it reads actually live.
        var queryNames = visuals.Select(v => v.QueryName).Distinct().ToList();
        var queries = await db.SubscriberQueries.AsNoTracking()
            .Where(q => queryNames.Contains(q.Name))
            .Select(q => new { q.Name, q.Sql, q.ObjectKeys, q.SubscriberKey })
            .ToListAsync(ct).ConfigureAwait(false);
        var queryByName = queries
            .GroupBy(q => q.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var matches = new List<QuestionMatch>(ranked.Count);
        foreach (var (text, visualKey, similarity) in ranked)
        {
            visualByKey.TryGetValue(visualKey, out var visual);
            var query = visual is not null && queryByName.TryGetValue(visual.QueryName, out var q) ? q : null;

            matches.Add(new QuestionMatch(
                text,
                query?.Sql ?? string.Empty,
                SplitObjectKeys(query?.ObjectKeys),
                PowerBiProvenance,
                similarity,
                query?.SubscriberKey ?? SubscriberKeyFromVisualKey(visualKey),
                visual?.Title));
        }

        return matches;
    }

    /// <summary>Splits the newline-joined object keys the catalog stores into a list, dropping blanks.</summary>
    private static IReadOnlyList<string> SplitObjectKeys(string? objectKeys)
        => string.IsNullOrWhiteSpace(objectKeys)
            ? []
            : objectKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Recovers the subscriber key from a visual key when the query row could not be resolved. A visual
    /// key is <c>&lt;subscriberKey&gt;#&lt;reportFile&gt;#&lt;page&gt;#&lt;visual&gt;</c>, so the subscriber is
    /// everything before the first '#'.</summary>
    private static string SubscriberKeyFromVisualKey(string visualKey)
    {
        var hash = visualKey.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? visualKey : visualKey[..hash];
    }
}
