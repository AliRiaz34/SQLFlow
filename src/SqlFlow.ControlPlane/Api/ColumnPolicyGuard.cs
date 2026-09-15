using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.SqlServer.Query;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The data-scope half of the ad-hoc query trust boundary: where <see cref="ReadOnlyQueryGuard"/> proves a
/// statement is a single read-only SELECT, this proves it reads only columns an admin has explicitly allow-listed
/// (see <see cref="CatalogColumnPolicy"/>). Both must pass before a statement may be prepared, confirmed as a
/// PowerAI example, or auto-run, so a query built by an LLM (which never sees a denied column's name, since
/// search/describe already filter them out) cannot reach one anyway by guessing or by an old stored example.
///
/// The model is default-deny, at two levels. First, a table reference the catalog has no object for at all - a
/// synonym, or anything else not harvested into the catalog - is refused outright: with no catalog object there
/// is no allow-list to check it against, so it cannot be let through unchecked the way a blacklist would. Second,
/// for a table the catalog DOES know, only a column with an explicit <see cref="CatalogColumnPolicy.IsAllowed"/>
/// row may be read; a column with no row, or an explicitly denied one, is refused. A common table expression's
/// own name is exempted from the first check (it is not a catalog object and never will be), but every real
/// table its body reads is still checked normally.
///
/// Table references are resolved to catalog objects by schema/name only (matching case-insensitively; an
/// unqualified reference matches any schema), never by the connection a query happens to run against, because a
/// policy is set once per object and must hold regardless of which datasource reference reaches it.
/// </summary>
public static class ColumnPolicyGuard
{
    /// <summary>
    /// Throws <see cref="SqlFlowException"/> naming the offending table or column when <paramref name="sql"/> (a
    /// statement <see cref="ReadOnlyQueryGuard"/> has already accepted) reads something not on the allow-list. A
    /// statement this parses differently than the guard already did passes: this is a second, narrower check
    /// layered on the guard's own parse, not a replacement for it.
    /// </summary>
    public static async Task EnsureAllowedAsync(CatalogDbContext db, string sql, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var select = ParseSingleSelect(sql);
        if (select is null)
        {
            return;
        }

        var accesses = SqlColumnAccessExtractor.Extract(select);
        if (accesses.Count == 0)
        {
            return;
        }

        // A CTE's own name parses identically to a real table reference, so it is excluded here by name: it is
        // never a catalog object, and denying it would refuse every query that uses a CTE. The real tables its
        // body reads still appear as their own accesses and are checked normally.
        var cteNames = (select.WithCtesAndXmlNamespaces?.CommonTableExpressions ?? [])
            .Select(c => c.ExpressionName.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tableNames = accesses.Select(a => a.Table.Name)
            .Where(name => !cteNames.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tableNames.Count == 0)
        {
            return;
        }

        var candidates = await db.Objects.AsNoTracking()
            .Where(o => tableNames.Contains(o.Name))
            .Select(o => new { o.Key, o.Schema, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        // Fail closed: a table name the catalog has no object for at all - a synonym is the case that motivated
        // this, but it applies to any object type the harvester does not track - has no allow-list to check, so
        // it is refused rather than passed through unchecked.
        foreach (var name in tableNames)
        {
            if (!candidates.Exists(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SqlFlowException(
                    $"'{name}' is not a table or view the catalog has a record of, so its column policy cannot " +
                    "be verified and it is refused. Sync the catalog first, or query a known object.");
            }
        }

        var candidateKeys = candidates.Select(c => c.Key).Distinct(StringComparer.Ordinal).ToList();
        var allowed = await db.ColumnPolicies.AsNoTracking()
            .Where(p => p.IsAllowed && candidateKeys.Contains(p.ObjectKey))
            .Select(p => new { p.ObjectKey, p.ColumnName })
            .ToListAsync(ct).ConfigureAwait(false);

        var allowedByKey = allowed
            .GroupBy(p => p.ObjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var objectColumns = await db.ObjectColumns.AsNoTracking()
            .Where(c => candidateKeys.Contains(c.ObjectKey))
            .Select(c => new { c.ObjectKey, c.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var columnsByKey = objectColumns
            .GroupBy(c => c.ObjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Name).ToList());

        foreach (var access in accesses)
        {
            if (cteNames.Contains(access.Table.Name))
            {
                continue;
            }

            foreach (var candidate in candidates.Where(c => access.Table.Matches(c.Schema, c.Name)))
            {
                allowedByKey.TryGetValue(candidate.Key, out var allowedColumns);
                allowedColumns ??= [];

                if (access.Column is null)
                {
                    columnsByKey.TryGetValue(candidate.Key, out var knownColumns);
                    var notAllowed = (knownColumns ?? []).Where(c => !allowedColumns.Contains(c)).ToList();
                    if (notAllowed.Count > 0)
                    {
                        throw new SqlFlowException(
                            $"'{candidate.Schema}.{candidate.Name}' has columns not on the allow-list " +
                            $"({string.Join(", ", notAllowed)}), so SELECT * (or an unqualified/table-qualified " +
                            "star) cannot be used against it here. Name only the allowed columns.");
                    }

                    continue;
                }

                if (!allowedColumns.Contains(access.Column))
                {
                    throw new SqlFlowException(
                        $"Column '{candidate.Schema}.{candidate.Name}.{access.Column}' is not on the allow-list " +
                        "and cannot be read here.");
                }
            }
        }
    }

    private static SelectStatement? ParseSingleSelect(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count > 0 || fragment is not TSqlScript script)
        {
            return null;
        }

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        return statements is [SelectStatement select] ? select : null;
    }
}
