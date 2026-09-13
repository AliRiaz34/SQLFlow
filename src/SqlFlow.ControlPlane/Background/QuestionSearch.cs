using Microsoft.EntityFrameworkCore;
using SqlFlow.Assistant;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// One stored question matched against a newly typed one: the question itself, the SQL that already answers it,
/// the warehouse objects that SQL reads, where the example came from, and how well it matched. The match score
/// is the trustworthy confidence signal (POWERAI.md Section 6: an LLM's self-rating is not, because a wrong
/// query can sound exactly as confident as a right one).
/// </summary>
/// <param name="Question">The stored question text.</param>
/// <param name="Sql">The query that answers it, empty when the visual's query could not be resolved.</param>
/// <param name="ObjectKeys">The warehouse objects <paramref name="Sql"/> reads.</param>
/// <param name="Provenance">Where the example came from: <c>powerbi</c> for a question derived from an
/// extracted report visual. The user-confirmed half of the store does not exist yet, so this is how a caller
/// tells them apart once it does, without the shape changing underneath it.</param>
/// <param name="Score">How many of the searched terms this question matched. Unbounded and relative to the
/// other matches in the same search rather than a 0-1 similarity.</param>
/// <param name="MatchedTerms">The searched terms this question actually contains, so a caller can show WHY it
/// matched rather than only how strongly.</param>
/// <param name="SubscriberKey">The report (subscriber) this question came from.</param>
/// <param name="VisualTitle">The title of the visual that answers it, when it has one.</param>
public sealed record QuestionMatch(
    string Question,
    string Sql,
    IReadOnlyList<string> ObjectKeys,
    string Provenance,
    int Score,
    IReadOnlyList<string> MatchedTerms,
    string SubscriberKey,
    string? VisualTitle);

/// <summary>The outcome of one search: the terms actually searched for (the expansion, or the question's own
/// words when expansion is off or unavailable) and the matches they found. The terms travel with the result so
/// a caller can show what was looked for, which is what makes an empty result interpretable rather than
/// mysterious.</summary>
/// <param name="SearchedTerms">The terms the stored questions were matched against.</param>
/// <param name="Matches">The best matches, best first.</param>
public sealed record QuestionSearchResult(
    IReadOnlyList<string> SearchedTerms, IReadOnlyList<QuestionMatch> Matches);

/// <summary>
/// Matches stored questions against a newly typed one (POWERAI.md Section 6). The mechanism is a word search,
/// not a vector one: an LLM first expands the typed question into the business vocabulary a stored question
/// might have used instead ("turnover" also yielding "revenue", "sales"), and the stored questions are then
/// ranked by how many of those terms they contain. SQL Server's own full-text engine supplies the linguistic
/// half (inflections such as "sell"/"selling"/"sold"), and the expansion supplies the business-vocabulary half
/// it cannot know.
/// <para>
/// The terms reach SQL Server through <see cref="EF.Functions"/>, which parameterizes them, rather than being
/// pasted into a predicate string: a model-authored fragment interpolated into <c>CONTAINS</c> syntax would be
/// both a correctness hazard (an apostrophe in "customer's" breaks the predicate) and injection-shaped.
/// </para>
/// </summary>
public static class QuestionSearch
{
    /// <summary>The provenance of a question derived from an extracted PowerBI report visual.</summary>
    public const string PowerBiProvenance = "powerbi";

    /// <summary>
    /// Returns the <paramref name="topK"/> stored questions best matching <paramref name="question"/>, best
    /// first. <paramref name="expander"/> is optional: without it (or when it fails) the search runs on the
    /// question's own words, which is weaker but still answers, so an Anthropic outage degrades retrieval
    /// rather than breaking it.
    /// </summary>
    /// <param name="db">The catalog to search.</param>
    /// <param name="question">The newly typed question to match stored examples against.</param>
    /// <param name="topK">How many matches to return, best first.</param>
    /// <param name="expander">Expands the question into business-vocabulary terms, or null to search the typed
    /// words alone.</param>
    /// <param name="repoId">Restricts the search to one repo, or searches the whole estate when null. Estate-wide
    /// is the useful default: a question about revenue is worth answering from whichever repo's report first
    /// asked it.</param>
    /// <param name="ct">Cancels the search.</param>
    public static async Task<QuestionSearchResult> FindSimilarAsync(
        CatalogDbContext db, string question, int topK, QuestionExpander? expander, Guid? repoId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var terms = await BuildTermsAsync(question, expander, ct).ConfigureAwait(false);
        return await FindSimilarAsync(db, question, topK, terms, repoId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The same search against terms already decided, for a caller that has its own expansion (and for tests,
    /// which supply terms directly rather than depending on a model's vocabulary). The terms are normalized
    /// here too, so a caller cannot accidentally search on stop words.
    /// </summary>
    /// <param name="db">The catalog to search.</param>
    /// <param name="question">The typed question, for context only; matching is on <paramref name="terms"/>.</param>
    /// <param name="topK">How many matches to return, best first.</param>
    /// <param name="terms">The vocabulary to match stored questions against.</param>
    /// <param name="repoId">Restricts the search to one repo, or the whole estate when null.</param>
    /// <param name="ct">Cancels the search.</param>
    public static async Task<QuestionSearchResult> FindSimilarAsync(
        CatalogDbContext db, string question, int topK, IReadOnlyList<string> terms, Guid? repoId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);

        terms = Normalize(terms);
        if (terms.Count == 0)
        {
            return new QuestionSearchResult([], []);
        }

        // One full-text predicate per term, OR-ed: a question matching more of them ranks higher. Built as a
        // single CONTAINS over an OR-of-terms string would let SQL Server rank internally, but that means
        // assembling search syntax from model output; scoring by counting matched terms keeps every term a
        // parameter and makes the score explainable (MatchedTerms says exactly which ones hit).
        var candidates = await MatchingQuestionsAsync(db, terms, repoId, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return new QuestionSearchResult(terms, []);
        }

        var ranked = candidates
            .Select(c => (c.Question, c.VisualKey, Matched: terms.Where(t => ContainsWord(c.Question, t)).ToList()))
            .Select(c => (c.Question, c.VisualKey, c.Matched, Score: c.Matched.Count))
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Question.Length)
            .Take(topK)
            .ToList();
        if (ranked.Count == 0)
        {
            return new QuestionSearchResult(terms, []);
        }

        var visualKeys = ranked.Select(r => r.VisualKey).Distinct().ToList();
        var visuals = await db.SubscriberReportVisuals.AsNoTracking()
            .Where(v => visualKeys.Contains(v.VisualKey))
            .Select(v => new { v.VisualKey, v.QueryName, v.Title })
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
        foreach (var (text, visualKey, matched, score) in ranked)
        {
            visualByKey.TryGetValue(visualKey, out var visual);
            var query = visual is not null && queryByName.TryGetValue(visual.QueryName, out var q) ? q : null;

            matches.Add(new QuestionMatch(
                text,
                query?.Sql ?? string.Empty,
                SplitObjectKeys(query?.ObjectKeys),
                PowerBiProvenance,
                score,
                matched,
                query?.SubscriberKey ?? SubscriberKeyFromVisualKey(visualKey),
                visual?.Title));
        }

        return new QuestionSearchResult(terms, matches);
    }

    /// <summary>
    /// Loads the stored questions containing any of <paramref name="terms"/>, using the full-text index when
    /// the instance has one and falling back to a plain <c>LIKE</c> scan when it does not. The fallback exists
    /// because full-text is an installable SQL Server feature the catalog cannot assume (its own migration
    /// skips the index where it is absent), and a deployment without it should get weaker matching rather than
    /// an error naming a feature its DBA may not be willing to install.
    /// </summary>
    private static async Task<List<QuestionRow>> MatchingQuestionsAsync(
        CatalogDbContext db, IReadOnlyList<string> terms, Guid? repoId, CancellationToken ct)
    {
        var scoped = db.SubscriberReportVisualQuestions.AsNoTracking()
            .Where(q => repoId == null || q.RepoId == repoId);

        if (await HasFullTextIndexAsync(db, ct).ConfigureAwait(false))
        {
            // EF.Functions.Contains parameterizes each term, so a term carrying an apostrophe or a full-text
            // operator is matched as text rather than changing the predicate.
            var predicate = terms.Aggregate(
                (System.Linq.Expressions.Expression<Func<CatalogSubscriberReportVisualQuestion, bool>>?)null,
                (acc, term) =>
                {
                    System.Linq.Expressions.Expression<Func<CatalogSubscriberReportVisualQuestion, bool>> one =
                        q => EF.Functions.Contains(q.Question, term);
                    return acc is null ? one : Or(acc, one);
                })!;

            return await scoped.Where(predicate)
                .Select(q => new QuestionRow(q.Question, q.VisualKey))
                .Take(MaxCandidates)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var like = terms.Aggregate(
            (System.Linq.Expressions.Expression<Func<CatalogSubscriberReportVisualQuestion, bool>>?)null,
            (acc, term) =>
            {
                System.Linq.Expressions.Expression<Func<CatalogSubscriberReportVisualQuestion, bool>> one =
                    q => q.Question.Contains(term);
                return acc is null ? one : Or(acc, one);
            })!;

        return await scoped.Where(like)
            .Select(q => new QuestionRow(q.Question, q.VisualKey))
            .Take(MaxCandidates)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Whether the stored questions carry a full-text index on this instance. Cached per process: it
    /// is a deployment property that changes only when the catalog is migrated or the feature is installed,
    /// both of which restart or redeploy the control plane.</summary>
    private static async Task<bool> HasFullTextIndexAsync(CatalogDbContext db, CancellationToken ct)
    {
        if (_hasFullText is { } known)
        {
            return known;
        }

        try
        {
            var present = await db.Database
                .SqlQuery<int>($@"
                    SELECT CASE WHEN SERVERPROPERTY('IsFullTextInstalled') = 1
                                 AND EXISTS (SELECT 1 FROM sys.fulltext_indexes
                                             WHERE object_id = OBJECT_ID(N'catalog.SubscriberReportVisualQuestion'))
                                THEN 1 ELSE 0 END AS [Value]")
                .SingleAsync(ct).ConfigureAwait(false);
            _hasFullText = present == 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A probe that cannot run (a restricted principal, an unusual provider) must not fail the search:
            // assume no index and use the LIKE path, which works on any instance.
            _hasFullText = false;
        }

        return _hasFullText.Value;
    }

    private static bool? _hasFullText;

    /// <summary>Resets the cached full-text probe. For tests, which migrate a catalog mid-process and so can
    /// see the index appear after a probe has already run.</summary>
    internal static void ResetFullTextProbe() => _hasFullText = null;

    /// <summary>The most stored questions one search pulls back before ranking. A ceiling rather than a page:
    /// past this many term hits the query is too broad for the extra rows to change the winners.</summary>
    private const int MaxCandidates = 500;

    private sealed record QuestionRow(string Question, string VisualKey);

    private static System.Linq.Expressions.Expression<Func<T, bool>> Or<T>(
        System.Linq.Expressions.Expression<Func<T, bool>> left,
        System.Linq.Expressions.Expression<Func<T, bool>> right)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(typeof(T), "q");
        var body = System.Linq.Expressions.Expression.OrElse(
            new ReplaceParameter(left.Parameters[0], parameter).Visit(left.Body)!,
            new ReplaceParameter(right.Parameters[0], parameter).Visit(right.Body)!);
        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private sealed class ReplaceParameter(
        System.Linq.Expressions.ParameterExpression from, System.Linq.Expressions.ParameterExpression to)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override System.Linq.Expressions.Expression VisitParameter(
            System.Linq.Expressions.ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }

    /// <summary>The terms to search for: the expansion when it produced any, otherwise the question's own
    /// meaningful words, so a search still runs when expansion is off or unreachable.</summary>
    private static async Task<IReadOnlyList<string>> BuildTermsAsync(
        string question, QuestionExpander? expander, CancellationToken ct)
    {
        if (expander is not null)
        {
            var expanded = await expander.ExpandAsync(question, ct).ConfigureAwait(false);
            if (expanded.Count > 0)
            {
                return Normalize(expanded);
            }
        }

        return Normalize(question.Split(
            [' ', '\t', '\n', '\r', '?', '!', ',', '.', ';', ':', '(', ')', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Trims, lowercases, drops stop words and one-character tokens, and de-duplicates. Stop words are
    /// dropped here rather than left to SQL Server because they would otherwise count toward a question's score
    /// and let "what is the" outrank a real vocabulary match.</summary>
    private static IReadOnlyList<string> Normalize(IEnumerable<string> terms)
        => terms
            .Select(t => t.Trim().Trim('"').ToLowerInvariant())
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "in", "on", "at", "to", "for", "by", "with", "from", "as",
        "is", "are", "was", "were", "be", "been", "do", "does", "did", "what", "which", "who", "whom",
        "how", "why", "when", "where", "our", "we", "us", "you", "your", "it", "its", "this", "that",
        "these", "those", "there", "their", "have", "has", "had", "can", "could", "would", "should",
        "many", "much", "most", "more", "per", "into", "out", "over", "up", "down", "about",
    };

    /// <summary>Whether a question contains a term as a whole word, so "sale" does not score against
    /// "wholesale". A term that is itself multi-word (a short noun phrase from the expansion) is matched as a
    /// substring, since its own boundaries already make it specific.</summary>
    private static bool ContainsWord(string question, string term)
    {
        if (term.Contains(' ', StringComparison.Ordinal))
        {
            return question.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        var index = 0;
        while ((index = question.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(question[index - 1]);
            var after = index + term.Length;
            var afterOk = after >= question.Length || !char.IsLetterOrDigit(question[after]);
            if (beforeOk && afterOk)
            {
                return true;
            }
            index = after;
        }

        return false;
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
