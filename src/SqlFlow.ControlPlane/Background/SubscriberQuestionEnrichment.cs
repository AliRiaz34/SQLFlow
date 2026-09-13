using Microsoft.EntityFrameworkCore;
using SqlFlow.Assistant;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Generates 1-3 natural-language business questions for each extracted PowerBI report visual, POWERAI.md's
/// "business-question field" (Section 10, item 3). This runs as a control-plane-only step AFTER
/// <see cref="CatalogSync.SyncAsync"/> has written a sync's <c>SubscriberReportVisual</c>/<c>Field</c> rows:
/// <c>tools/pbix-extract</c> stays a pure parser (no network, no LLM, matching its "untrusted input" security
/// posture), and <c>CatalogSync</c>/<c>SqlFlow.Lineage</c> stay exactly as they were, since both are shared code
/// the bare CLI's <c>sqlflow db sync</c> also runs with no Anthropic wiring at all.
/// <para>
/// Regeneration is incremental: every sync deletes and reinserts ALL of a repo's subscriber report rows wholesale
/// (<see cref="CatalogSync"/>'s own reconciliation), so the only way to tell "the same visual as last sync" from
/// "a new one" is <see cref="CatalogSubscriberReportVisual.ContentHash"/>. The caller takes a snapshot of a
/// repo's visuals and their questions BEFORE calling <c>SyncAsync</c> (<see cref="SnapshotAsync"/>), then this
/// step compares each freshly-synced visual's hash against that snapshot: an unchanged hash carries the old
/// questions forward with no LLM call; a new or changed hash calls <see cref="QuestionGenerator"/>.
/// </para>
/// </summary>
public static class SubscriberQuestionEnrichment
{
    /// <summary>One visual's prior state, read before a sync deletes and reinserts it: its content hash (to
    /// detect whether the fresh row is the same visual, unchanged) and the questions to carry forward when it is.</summary>
    public sealed record VisualSnapshot(string ContentHash, IReadOnlyList<PriorQuestion> Questions);

    /// <summary>One carried-forward question and the embedding it already had. The vector travels with the text
    /// so an unchanged visual costs neither an LLM call nor an embeddings call: re-embedding identical text
    /// would produce the identical vector at a price.</summary>
    public sealed record PriorQuestion(string Question, byte[]? Embedding, string? EmbeddingModel, DateTime? EmbeddedAtUtc);

    /// <summary>Reads this repo's current subscriber-visual questions, keyed by <c>VisualKey</c>, so they can be
    /// carried forward after the sync that is about to delete and reinsert every visual row. Call this
    /// immediately BEFORE <see cref="CatalogSync.SyncAsync"/>.</summary>
    public static async Task<IReadOnlyDictionary<string, VisualSnapshot>> SnapshotAsync(
        CatalogDbContext db, Guid repoId, CancellationToken ct)
    {
        var visuals = await db.SubscriberReportVisuals.AsNoTracking()
            .Where(v => v.RepoId == repoId)
            .Select(v => new { v.VisualKey, v.ContentHash })
            .ToListAsync(ct).ConfigureAwait(false);
        if (visuals.Count == 0)
        {
            return new Dictionary<string, VisualSnapshot>();
        }

        var visualKeys = visuals.Select(v => v.VisualKey).ToList();
        var questionsByVisual = (await db.SubscriberReportVisualQuestions.AsNoTracking()
                .Where(q => visualKeys.Contains(q.VisualKey))
                .OrderBy(q => q.Ordinal)
                .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(q => q.VisualKey);

        return visuals.ToDictionary(
            v => v.VisualKey,
            v => new VisualSnapshot(
                v.ContentHash,
                questionsByVisual[v.VisualKey]
                    .Select(q => new PriorQuestion(q.Question, q.Embedding, q.EmbeddingModel, q.EmbeddedAtUtc))
                    .ToArray()));
    }

    /// <summary>
    /// For every visual this sync just wrote for <paramref name="repoId"/>: carries its prior questions forward
    /// when <paramref name="before"/> shows the same <c>ContentHash</c>, otherwise calls
    /// <paramref name="generator"/> for fresh ones. Writes directly (its own <c>SaveChangesAsync</c>), after the
    /// sync's own transaction has already committed, so a generation failure here never rolls back the visuals
    /// and fields the sync itself wrote. Returns the warnings collected (a visual whose questions could not be
    /// generated), for the caller to surface the same way a sync's own warnings are surfaced.
    /// </summary>
    public static async Task<IReadOnlyList<string>> EnrichAsync(
        CatalogDbContext db, Guid repoId, IReadOnlyDictionary<string, VisualSnapshot> before,
        QuestionGenerator generator, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(generator);

        var visuals = await db.SubscriberReportVisuals.AsNoTracking()
            .Where(v => v.RepoId == repoId)
            .ToListAsync(ct).ConfigureAwait(false);
        if (visuals.Count == 0)
        {
            return [];
        }

        var visualKeys = visuals.Select(v => v.VisualKey).ToList();
        var fieldsByVisual = (await db.SubscriberReportFields.AsNoTracking()
                .Where(f => visualKeys.Contains(f.VisualKey))
                .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(f => f.VisualKey);

        var warnings = new List<string>();

        foreach (var visual in visuals)
        {
            IReadOnlyList<PriorQuestion> questions;

            if (before.TryGetValue(visual.VisualKey, out var prior) && prior.ContentHash == visual.ContentHash)
            {
                // Unchanged since the last sync: carry the old questions forward, embeddings included, with no
                // LLM call and nothing left for the embedding step below to recompute.
                questions = prior.Questions;
            }
            else
            {
                var context = new VisualQuestionContext(
                    visual.Title,
                    visual.VisualType,
                    fieldsByVisual[visual.VisualKey]
                        .Select(f => new VisualQuestionField(f.Role, f.TableName, f.ColumnOrMeasure, f.IsMeasure))
                        .ToArray());
                // Freshly generated text has no embedding yet; the embedding step picks these up.
                questions = (await generator.GenerateQuestionsAsync(context, ct).ConfigureAwait(false))
                    .Select(q => new PriorQuestion(q, null, null, null))
                    .ToArray();

                if (questions.Count == 0)
                {
                    warnings.Add(
                        $"subscriber report visual '{visual.Title ?? visual.VisualType}' ({visual.VisualKey}): "
                        + "no business questions could be generated; it was left without any.");
                    // Nothing to write for this visual; move on rather than clearing rows that do not exist.
                    continue;
                }
            }

            // Replace this visual's questions: they are repo-scoped child rows keyed by VisualKey, the same
            // wholesale-replace shape CatalogSync itself uses for the row above them.
            await db.SubscriberReportVisualQuestions
                .Where(q => q.RepoId == repoId && q.VisualKey == visual.VisualKey)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

            var ordinal = 0;
            foreach (var question in questions)
            {
                ordinal++;
                db.SubscriberReportVisualQuestions.Add(new CatalogSubscriberReportVisualQuestion
                {
                    RepoId = repoId,
                    VisualKey = visual.VisualKey,
                    Ordinal = ordinal,
                    Question = question.Question,
                    Embedding = question.Embedding,
                    EmbeddingModel = question.EmbeddingModel,
                    EmbeddedAtUtc = question.EmbeddedAtUtc,
                });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return warnings;
    }

    /// <summary>
    /// Embeds every one of this repo's stored questions that lacks a current vector, so a newly typed question
    /// can be ranked against them by cosine similarity (POWERAI.md Section 6). A row needs embedding when it has
    /// none at all (freshly generated text) or when its <c>EmbeddingModel</c> is not
    /// <paramref name="embedder"/>'s: vectors from two models are not comparable, so a model change re-embeds
    /// exactly the affected rows rather than the whole table.
    /// <para>
    /// Deliberately separate from <see cref="EnrichAsync"/> and gated by its own switch, because retrieval and
    /// question generation are independently toggleable: a deployment that generated questions earlier and only
    /// now turns retrieval on has a table full of un-embedded rows, and this step is what fills them in without
    /// a second LLM pass. Like generation, it runs after the sync's own transaction has committed, and a
    /// failure degrades to a returned warning rather than an exception, so an embeddings outage never costs the
    /// structural rows the sync already wrote.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<string>> EmbedQuestionsAsync(
        CatalogDbContext db, Guid repoId, IEmbeddingProvider embedder, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(clock);

        var model = embedder.Model;
        var stale = await db.SubscriberReportVisualQuestions
            .Where(q => q.RepoId == repoId && (q.Embedding == null || q.EmbeddingModel != model))
            .Select(q => new { q.Id, q.Question })
            .ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count == 0)
        {
            return [];
        }

        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await embedder.EmbedAsync(stale.Select(q => q.Question).ToArray(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return
            [
                $"{stale.Count} stored question(s) could not be embedded ({ex.Message}); "
                + "they keep whatever vector they had and are retried on the next sync.",
            ];
        }

        var embeddedAt = clock.GetUtcNow().UtcDateTime;
        for (var i = 0; i < stale.Count; i++)
        {
            var bytes = EmbeddingMath.ToBytes(vectors[i]);
            await db.SubscriberReportVisualQuestions
                .Where(q => q.Id == stale[i].Id)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(q => q.Embedding, bytes)
                        .SetProperty(q => q.EmbeddingModel, model)
                        .SetProperty(q => q.EmbeddedAtUtc, embeddedAt),
                    ct)
                .ConfigureAwait(false);
        }

        return [];
    }
}
