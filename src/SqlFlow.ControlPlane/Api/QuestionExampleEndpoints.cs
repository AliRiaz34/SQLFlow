using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core;
using SqlFlow.SqlServer.Query;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// What a person decided about a proposed answer. POWERAI.md Section 6's loop is accept/correct/reject, and all
/// three arrive here: the first two are knowledge worth keeping, and the third is recorded as having happened
/// without being kept as fact.
/// </summary>
public static class QuestionConfirmationOutcome
{
    /// <summary>The proposed query answered the question as written.</summary>
    public const string Accepted = "accepted";

    /// <summary>The query was edited before it answered the question. What is stored is the CORRECTED query, so
    /// the example the estate learns from is the one that actually worked.</summary>
    public const string Corrected = "corrected";

    /// <summary>The query did not answer the question. Nothing is stored: POWERAI.md Section 6 is explicit that
    /// on rejection nothing is learned as fact.</summary>
    public const string Rejected = "rejected";

    /// <summary>Whether <paramref name="value"/> is one of the three outcomes, case-insensitively.</summary>
    public static bool IsKnown(string? value)
        => string.Equals(value, Accepted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Corrected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Rejected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One confirmation of a question/query pair: what was asked, the query that was judged, what the person
/// decided, and the retrieval score the proposal was built from.
/// </summary>
/// <param name="Question">The question as it was asked.</param>
/// <param name="Sql">The query being confirmed. On <see cref="QuestionConfirmationOutcome.Corrected"/> this is
/// the corrected text, not the original proposal: the store holds what worked, never what was fixed.</param>
/// <param name="Outcome">One of <see cref="QuestionConfirmationOutcome"/>'s values.</param>
/// <param name="ObjectKeys">The warehouse objects the query reads, when the caller resolved them. Optional: an
/// example is still worth keeping without them, and inventing keys that no lineage pass produced would put
/// unverified identities into the catalog.</param>
/// <param name="Confidence">The retrieval score the confirmed answer was built from, or null when it was
/// composed without a prior match. Never a model's own estimate of its correctness.</param>
/// <param name="RepoId">The repo to attribute the example to, or null to keep it estate-wide.</param>
public sealed record ConfirmQuestionRequest(
    string? Question, string? Sql, string? Outcome, IReadOnlyList<string>? ObjectKeys = null,
    int? Confidence = null, Guid? RepoId = null);

/// <summary>
/// What the confirmation did. <see cref="Stored"/> is the fact that matters: a rejection is accepted by the
/// endpoint and stores nothing, so a caller must read this rather than infer from the 200 that the estate
/// learned something.
/// </summary>
/// <param name="Stored">Whether an example was written or refreshed.</param>
/// <param name="ExampleId">The row that now holds the example, or null when nothing was stored.</param>
/// <param name="Outcome">The outcome as it was recorded, normalized to lower case.</param>
/// <param name="Provenance">The provenance the stored example carries, or null when nothing was stored.</param>
/// <param name="Message">What happened, in a sentence a caller can show a person verbatim.</param>
public sealed record ConfirmedQuestionDto(
    bool Stored, long? ExampleId, string Outcome, string? Provenance, string Message);

/// <summary>
/// The write half of PowerAI retrieval: the confirmed-example store POWERAI.md Sections 6 and 8 specify, and
/// the endpoint that grows it from real usage. Retrieval without this reads only what PowerBI extraction
/// derived, so the system never gets better at the questions people actually ask; with it, an answer a person
/// checked today is precedent the next similar question is matched against.
/// <para>
/// It lives under the "operate" scope rather than the read group because it WRITES estate knowledge that later
/// answers are grounded in. A caller who can add confirmed examples can steer every future answer toward a
/// query of their choosing, which is a privilege, not a read.
/// </para>
/// <para>
/// It is deliberately NOT a second execution path: nothing here runs a query or reaches a datasource. The
/// statement is parsed to prove it is read-only (the same <see cref="ReadOnlyQueryGuard"/> the DataOps prepare
/// step uses, so the store cannot come to hold a query that would write if anyone ran it), then stored as text.
/// Running it still goes through prepare/run and their human gate, unchanged.
/// </para>
/// </summary>
public static class QuestionExampleEndpoints
{
    public static RouteGroupBuilder MapQuestionExampleEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/powerai/questions/confirm", ConfirmAsync)
            .WithTags("PowerAI").WithName("ConfirmQuestionExample");

        return group;
    }

    /// <summary>
    /// Records what a person decided about a question/query pair, storing the pair as a confirmed example when
    /// they accepted or corrected it. Confirming the same pair again refreshes the existing row rather than
    /// adding a duplicate, so reaffirming an answer does not give it extra weight in every later search.
    /// </summary>
    private static async Task<Results<Ok<ConfirmedQuestionDto>, ProblemHttpResult>> ConfirmAsync(
        ConfirmQuestionRequest request, CatalogDbContext db, IOptions<ControlPlaneOptions> options,
        TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (request is null)
        {
            return Problem("A confirmation requires a request body.", StatusCodes.Status400BadRequest);
        }

        // The same gate the search side answers 501 on. The store exists to be searched, so a deployment that
        // has not enabled retrieval has not opted into this feature at all, and silently accumulating
        // confirmations nobody can ever retrieve would be worse than saying so.
        if (!options.Value.PowerAI.Retrieval.Enabled)
        {
            return Problem(
                "Question retrieval is not enabled on this deployment "
                + "(ControlPlane:PowerAI:Retrieval:Enabled), so there is no example store to confirm into.",
                StatusCodes.Status501NotImplemented, "Retrieval not configured");
        }

        if (!QuestionConfirmationOutcome.IsKnown(request.Outcome))
        {
            return Problem(
                $"Unknown outcome '{request.Outcome}'. Valid values: "
                + $"{QuestionConfirmationOutcome.Accepted}, {QuestionConfirmationOutcome.Corrected}, "
                + $"{QuestionConfirmationOutcome.Rejected}.",
                StatusCodes.Status400BadRequest);
        }

        var outcome = request.Outcome!.Trim().ToLowerInvariant();
        var question = request.Question?.Trim() ?? string.Empty;
        if (question.Length == 0)
        {
            return Problem("The question being confirmed must not be empty.", StatusCodes.Status400BadRequest);
        }

        if (question.Length > MaxQuestionLength)
        {
            return Problem(
                $"The question is {question.Length} characters, over the {MaxQuestionLength}-character limit.",
                StatusCodes.Status400BadRequest);
        }

        // A rejection is answered before the SQL is validated: the caller is telling us the query was WRONG,
        // and refusing their report because the wrong query also failed to parse would lose the one signal the
        // exchange carried. Nothing is stored either way, so nothing depends on the text being well-formed.
        if (string.Equals(outcome, QuestionConfirmationOutcome.Rejected, StringComparison.Ordinal))
        {
            return TypedResults.Ok(new ConfirmedQuestionDto(
                Stored: false, ExampleId: null, outcome, Provenance: null,
                "The rejection was recorded and nothing was stored: a refuted query is not knowledge, so it "
                + "never becomes an example later answers are grounded in."));
        }

        string sql;
        try
        {
            // Parsed, not pattern-matched, and for the same reason the prepare step parses: an example is a
            // query later answers get adapted from, so one that would write if it ran must never enter the
            // store, however it was labelled on the way in.
            sql = ReadOnlyQueryGuard.Validate(request.Sql ?? string.Empty);
        }
        catch (SqlFlowException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest, "Query refused");
        }

        if (request.RepoId is { } repoId)
        {
            var exists = await db.Repos.AsNoTracking().AnyAsync(r => r.Id == repoId, ct).ConfigureAwait(false);
            if (!exists)
            {
                return Problem($"No repo '{repoId}' exists in the catalog.", StatusCodes.Status404NotFound);
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var confirmedBy = user.FindFirst("sub")?.Value ?? user.Identity?.Name;
        var hash = QuestionExampleHash.Compute(question, sql);

        var existing = await db.QuestionExamples
            .FirstOrDefaultAsync(e => e.ContentHash == hash, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // Reaffirming an answer updates who last stood behind it and when, and takes the newer confidence
            // when one was supplied. The question and SQL are not rewritten: they are what the hash is OF, so
            // a change to either is a different example rather than an edit to this one.
            existing.ConfirmedUtc = now;
            existing.ConfirmedBy = confirmedBy;
            existing.Provenance = QuestionExampleProvenance.UserConfirmed;
            existing.Confidence = request.Confidence ?? existing.Confidence;
            if (request.ObjectKeys is { Count: > 0 })
            {
                existing.ObjectKeys = JoinObjectKeys(request.ObjectKeys);
            }

            if (request.RepoId is not null)
            {
                existing.RepoId = request.RepoId;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return TypedResults.Ok(new ConfirmedQuestionDto(
                Stored: true, existing.Id, outcome, existing.Provenance,
                "This question and query were already a confirmed example; the existing one was refreshed "
                + "rather than stored twice, so reaffirming it does not give it extra weight in later searches."));
        }

        var example = new CatalogQuestionExample
        {
            RepoId = request.RepoId,
            Question = question,
            Sql = sql,
            ObjectKeys = JoinObjectKeys(request.ObjectKeys),
            Provenance = QuestionExampleProvenance.UserConfirmed,
            Confidence = request.Confidence,
            ConfirmedUtc = now,
            ConfirmedBy = confirmedBy,
            ContentHash = hash,
        };

        db.QuestionExamples.Add(example);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two people confirming the same answer at once: the unique index on the hash is what decides, and
            // the loser reports the winner's row rather than failing a confirmation that did land. Any other
            // write failure is rethrown, so a real problem is not disguised as a successful confirmation.
            db.Entry(example).State = EntityState.Detached;
            var winner = await db.QuestionExamples.AsNoTracking()
                .FirstOrDefaultAsync(e => e.ContentHash == hash, ct).ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            return TypedResults.Ok(new ConfirmedQuestionDto(
                Stored: true, winner.Id, outcome, winner.Provenance,
                "This question and query were confirmed concurrently by someone else; that example is the one "
                + "now stored."));
        }

        return TypedResults.Ok(new ConfirmedQuestionDto(
            Stored: true, example.Id, outcome, example.Provenance,
            "Stored as a confirmed example. Questions that mean the same thing will now find this query, and "
            + "the match will carry that a person confirmed it."));
    }

    /// <summary>Joins the object keys the way the catalog stores them everywhere else (newline-separated, blanks
    /// dropped), so a confirmed example's keys read back exactly like a subscriber query's.</summary>
    private static string JoinObjectKeys(IReadOnlyList<string>? objectKeys)
        => objectKeys is null
            ? string.Empty
            : string.Join('\n', objectKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim()));

    /// <summary>The longest question stored, matching the column's own length so an over-long question is
    /// refused with a stated reason rather than truncated into a different question.</summary>
    private const int MaxQuestionLength = 1000;

    private static ProblemHttpResult Problem(string detail, int statusCode, string title = "Invalid request")
        => TypedResults.Problem(detail: detail, statusCode: statusCode, title: title);
}
