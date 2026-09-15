using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// A saved answer (a stored question example) as the management page shows it.
/// </summary>
/// <param name="Id">The example's id, the one <c>find_similar_questions</c> reports as <c>exampleId</c>.</param>
/// <param name="Question">The question it answers.</param>
/// <param name="Sql">The query that answers it.</param>
/// <param name="SourceRef">The datasource it runs against, or null when none is stored and none can be inferred.</param>
/// <param name="ObjectKeys">The warehouse objects the query reads.</param>
/// <param name="Provenance">Where the example came from.</param>
/// <param name="Confidence">The retrieval score the answer was built from, when it was built from a match.</param>
/// <param name="RepoId">The repo it is attributed to, or null when estate-wide.</param>
/// <param name="ConfirmedBy">Who last stood behind it: the person who confirmed it, or the admin who last edited it.</param>
/// <param name="ConfirmedUtc">When that happened.</param>
/// <param name="Problem">Why the assistant is not currently offered this example (its SQL is no longer a single
/// read-only SELECT over allow-listed columns), or null when it is served.</param>
public sealed record QuestionExampleAdminDto(
    long Id, string Question, string Sql, string? SourceRef, IReadOnlyList<string> ObjectKeys, string Provenance,
    int? Confidence, Guid? RepoId, string? ConfirmedBy, DateTime ConfirmedUtc, string? Problem);

/// <summary>
/// An edit to a saved answer.
/// </summary>
/// <param name="Question">The question, as a person would type it.</param>
/// <param name="Sql">The query that answers it; refused unless it is a single read-only SELECT over allow-listed
/// columns, exactly as a confirmation is.</param>
/// <param name="SourceRef">The datasource to run it against, or blank to work it out from the tables the query reads.</param>
public sealed record UpdateQuestionExampleRequest(string? Question, string? Sql, string? SourceRef);

/// <summary>
/// Managing the saved answers the PowerAI assistant reuses: list and search them, correct one, or delete one that
/// should no longer be precedent. Confirming stays the only way an answer is ADDED (<see cref="QuestionExampleEndpoints"/>);
/// this surface only curates what is already stored.
/// <para>
/// It lives under the "admin" scope, not "operate" like confirming. Confirming adds a pair a person just checked in
/// their own conversation; editing or deleting rewrites precedent other people's answers are grounded in, and a
/// trusted example auto-runs with no approval, so changing one is estate curation rather than a user's own verdict.
/// </para>
/// <para>
/// An edit goes through the same validation a confirmation does (<see cref="QuestionExampleEndpoints.QuestionProblem"/>,
/// <see cref="QuestionExampleEndpoints.ValidateSqlAsync"/>, <see cref="QuestionExampleEndpoints.SourceRefProblemAsync"/>),
/// so the store cannot come to hold anything through this door that the confirm door would refuse.
/// </para>
/// </summary>
public static class QuestionExampleAdminEndpoints
{
    public static RouteGroupBuilder MapQuestionExampleAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var questions = group.MapGroup("/powerai/questions").WithTags("PowerAI");
        questions.MapGet(string.Empty, ListAsync).WithName("ListQuestionExamples");
        questions.MapGet("/{id:long}", GetAsync).WithName("GetQuestionExample");
        questions.MapPut("/{id:long}", UpdateAsync).WithName("UpdateQuestionExample");
        questions.MapDelete("/{id:long}", DeleteAsync).WithName("DeleteQuestionExample");

        return group;
    }

    /// <summary>Saved answers, newest first, optionally narrowed to those whose question or SQL contains every word of
    /// <paramref name="search"/>.</summary>
    private static async Task<Ok<PagedResult<QuestionExampleAdminDto>>> ListAsync(
        CatalogDbContext db, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = db.QuestionExamples.AsNoTracking();

        var term = SearchQuery.Parse(search);
        if (term is not null)
        {
            foreach (var token in term.Tokens)
            {
                var t = token;
                query = query.Where(e => e.Question.Contains(t) || e.Sql.Contains(t));
            }
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(e => e.ConfirmedUtc).ThenByDescending(e => e.Id)
            .Skip((p - 1) * size).Take(size)
            .ToListAsync(ct).ConfigureAwait(false);

        // Evaluated one at a time on the page's own rows (at most the page size): each check reads the column
        // policy, and the whole point of showing it is that a column denied after an answer was saved withholds it.
        var items = new List<QuestionExampleAdminDto>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(await ToDtoAsync(db, row, ct).ConfigureAwait(false));
        }

        return TypedResults.Ok(new PagedResult<QuestionExampleAdminDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<QuestionExampleAdminDto>, ProblemHttpResult>> GetAsync(
        long id, CatalogDbContext db, CancellationToken ct)
    {
        var example = await db.QuestionExamples.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        return example is null
            ? NotFound(id)
            : TypedResults.Ok(await ToDtoAsync(db, example, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Corrects a saved answer. The question and SQL are what the duplicate hash is OF, so it is recomputed, and an
    /// edit that would make this answer identical to another saved one is refused rather than silently merged. When
    /// the SQL changes, the objects it reads are resolved again from its tables (the old keys described a different
    /// query); an unchanged SQL keeps the keys it was stored with, which may have come from lineage the SQL alone
    /// cannot reproduce. A blank datasource is worked out from those objects, the same inference confirming uses.
    /// The editor becomes the person standing behind the answer, because a trusted example auto-runs on the strength
    /// of someone having checked exactly this SQL.
    /// </summary>
    private static async Task<Results<Ok<QuestionExampleAdminDto>, ProblemHttpResult>> UpdateAsync(
        long id, UpdateQuestionExampleRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Invalid("A request body is required.");
        }

        var question = QuestionExampleEndpoints.NormalizeQuestion(request.Question);
        if (QuestionExampleEndpoints.QuestionProblem(question) is { } questionProblem)
        {
            return Invalid(questionProblem);
        }

        string sql;
        try
        {
            sql = await QuestionExampleEndpoints.ValidateSqlAsync(db, request.Sql, ct).ConfigureAwait(false);
        }
        catch (SqlFlowException ex)
        {
            return TypedResults.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Query refused");
        }

        var sourceRef = SemanticLayer.TrimToNull(request.SourceRef);
        if (sourceRef is not null
            && await QuestionExampleEndpoints.SourceRefProblemAsync(db, sourceRef, ct).ConfigureAwait(false) is { } sourceRefProblem)
        {
            return sourceRefProblem;
        }

        var example = await db.QuestionExamples.AsTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (example is null)
        {
            return NotFound(id);
        }

        var hash = QuestionExampleHash.Compute(question, sql);
        if (await IsDuplicateAsync(db, hash, id, ct).ConfigureAwait(false))
        {
            return Duplicate();
        }

        // Compared through the hash's own normalization (case and whitespace folded), so reformatting the SQL is not
        // mistaken for a different query whose objects need resolving again.
        var sqlChanged = !string.Equals(
            QuestionExampleHash.Compute(string.Empty, example.Sql),
            QuestionExampleHash.Compute(string.Empty, sql),
            StringComparison.Ordinal);
        if (sqlChanged)
        {
            example.ObjectKeys = QuestionExampleEndpoints.JoinObjectKeys(
                await DatasourceInference.ObjectKeysFromSqlAsync(db, sql, ct).ConfigureAwait(false));
        }

        example.SourceRef = sourceRef
            ?? (await DatasourceInference.InferAsync(
                db, sql, QuestionExampleEndpoints.SplitObjectKeys(example.ObjectKeys), ct).ConfigureAwait(false)).Reference;
        example.Question = question;
        example.Sql = sql;
        example.ContentHash = hash;
        example.Provenance = QuestionExampleProvenance.UserConfirmed;
        example.ConfirmedBy = SemanticLayer.Actor(user);
        example.ConfirmedUtc = clock.GetUtcNow().UtcDateTime;

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The duplicate check above and the save are not atomic: the unique index on the hash is what finally
            // decides, and a loss to a concurrent write of the same pair is reported as the conflict it is.
            db.ChangeTracker.Clear();
            if (await IsDuplicateAsync(db, hash, id, ct).ConfigureAwait(false))
            {
                return Duplicate();
            }

            throw;
        }

        return TypedResults.Ok(await ToDtoAsync(db, example, ct).ConfigureAwait(false));
    }

    /// <summary>Deletes a saved answer. Later similar questions stop finding it, and it can no longer be auto-run.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        long id, CatalogDbContext db, CancellationToken ct)
    {
        var deleted = await db.QuestionExamples.Where(e => e.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return deleted == 0 ? NotFound(id) : TypedResults.NoContent();
    }

    private static Task<bool> IsDuplicateAsync(CatalogDbContext db, string hash, long id, CancellationToken ct)
        => db.QuestionExamples.AsNoTracking().AnyAsync(e => e.ContentHash == hash && e.Id != id, ct);

    private static async Task<QuestionExampleAdminDto> ToDtoAsync(
        CatalogDbContext db, CatalogQuestionExample example, CancellationToken ct)
        => new(
            example.Id, example.Question, example.Sql, example.SourceRef,
            QuestionExampleEndpoints.SplitObjectKeys(example.ObjectKeys), example.Provenance, example.Confidence,
            example.RepoId, example.ConfirmedBy, example.ConfirmedUtc,
            await SemanticLayer.QueryProblemAsync(db, example.Sql, ct).ConfigureAwait(false));

    private static ProblemHttpResult Invalid(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");

    private static ProblemHttpResult NotFound(long id)
        => TypedResults.Problem(
            detail: $"No saved answer with id '{id.ToString(CultureInfo.InvariantCulture)}'.",
            statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult Duplicate()
        => TypedResults.Problem(
            detail: "Another saved answer already has this question and query. Delete one of them instead of making "
                + "them identical.",
            statusCode: StatusCodes.Status409Conflict, title: "Duplicate saved answer");
}
