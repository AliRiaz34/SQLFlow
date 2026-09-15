using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.SqlServer.Query;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The data-scope half of the ad-hoc query trust boundary: where <see cref="ReadOnlyQueryGuard"/> proves a
/// statement is a single read-only SELECT, this proves it does not read a column an admin has marked sensitive
/// (see <see cref="CatalogColumnPolicy"/>). Both must pass before a statement may be prepared, confirmed as a
/// PowerAI example, or auto-run, so a query built by an LLM (which never sees a sensitive column's name, since
/// search/describe already filter them out) cannot reach one anyway by guessing or by an old stored example.
///
/// Table references are resolved to catalog objects by schema/name only (matching case-insensitively; an
/// unqualified reference matches any schema), never by the connection a query happens to run against, because a
/// policy is set once per object and must hold regardless of which datasource reference reaches it.
/// </summary>
public static class ColumnPolicyGuard
{
    /// <summary>
    /// Throws <see cref="SqlFlowException"/> naming the restricted column when <paramref name="sql"/> (a
    /// statement <see cref="ReadOnlyQueryGuard"/> has already accepted) reads one. A statement this parses
    /// differently than the guard already did, or that names no table the catalog recognizes, passes: this is a
    /// second, narrower check layered on the guard's own parse, not a replacement for it.
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

        var tableNames = accesses.Select(a => a.Table.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var candidates = await db.Objects.AsNoTracking()
            .Where(o => tableNames.Contains(o.Name))
            .Select(o => new { o.Key, o.Schema, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return;
        }

        var candidateKeys = candidates.Select(c => c.Key).ToList();
        var sensitive = await db.ColumnPolicies.AsNoTracking()
            .Where(p => p.IsSensitive && candidateKeys.Contains(p.ObjectKey))
            .Select(p => new { p.ObjectKey, p.ColumnName })
            .ToListAsync(ct).ConfigureAwait(false);
        if (sensitive.Count == 0)
        {
            return;
        }

        var sensitiveByKey = sensitive
            .GroupBy(p => p.ObjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase));

        foreach (var access in accesses)
        {
            foreach (var candidate in candidates.Where(c => access.Table.Matches(c.Schema, c.Name)))
            {
                if (!sensitiveByKey.TryGetValue(candidate.Key, out var sensitiveColumns))
                {
                    continue;
                }

                if (access.Column is null)
                {
                    throw new SqlFlowException(
                        $"'{candidate.Schema}.{candidate.Name}' has one or more restricted columns " +
                        $"({string.Join(", ", sensitiveColumns)}), so SELECT * (or an unqualified/table-qualified " +
                        "star) cannot be used against it here. Name only the columns you need.");
                }

                if (sensitiveColumns.Contains(access.Column))
                {
                    throw new SqlFlowException(
                        $"Column '{candidate.Schema}.{candidate.Name}.{access.Column}' is restricted and cannot " +
                        "be read here.");
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
