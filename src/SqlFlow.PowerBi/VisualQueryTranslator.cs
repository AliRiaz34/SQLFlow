using System.Globalization;
using System.Text;

namespace SqlFlow.PowerBi;

/// <summary>
/// Renders a visual's query, and the filters that apply to it, as one T-SQL SELECT.
/// <para>
/// There is ONE translator rather than two because a PowerBI filter is written in the same expression language
/// as the query it constrains: the same <c>Column</c>, <c>SourceRef</c>, <c>Literal</c>, <c>In</c> and
/// <c>Not</c> nodes appear in both. Folding the filters into the statement's WHERE clause is what keeps a
/// filtered visual an honest record of its question: a chart of sales restricted to one business type asks a
/// narrower question than the same chart unrestricted, and storing the restriction anywhere other than the
/// query would leave something that still has to be interpreted later.
/// </para>
/// <para>
/// The output exists to be READ, by the estate's existing T-SQL lineage parser and by a person: it names the
/// entities the visual reads so the objects resolve to the same catalog nodes the loading flows write. It is
/// not meant to be executed against the warehouse, and it deliberately carries no database or schema
/// qualification, since a visual names model entities and the resolution to physical objects (including
/// through old-production compatibility views) is the catalog's job, not this renderer's.
/// </para>
/// </summary>
public static class VisualQueryTranslator
{
    /// <summary>
    /// Renders one visual as a SELECT whose FROM names the entities it reads, whose SELECT list carries its
    /// projected fields and measures, and whose WHERE carries every filter that applies to it (the visual's
    /// own, plus the page-level filters passed in).
    /// </summary>
    public static string Translate(ReportVisual visual, IReadOnlyList<ReportFilter> pageFilters)
    {
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(pageFilters);

        var aliasEntities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in visual.Query.From)
        {
            aliasEntities[source.Alias] = source.Entity;
        }

        var builder = new StringBuilder();

        builder.Append("SELECT ");
        if (visual.Query.Select.Count == 0)
        {
            // Cannot happen for a visual the reader returned (it requires at least one resolved field), and is
            // rendered as a valid statement rather than an empty list so the output always parses.
            builder.Append('*');
        }
        else
        {
            for (var i = 0; i < visual.Query.Select.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                var selection = visual.Query.Select[i];
                builder.Append(Render(selection.Expression, aliasEntities));
                builder.Append(" AS ").Append(Quote(selection.Name));
            }
        }

        if (visual.Query.From.Count > 0)
        {
            builder.Append(" FROM ");
            for (var i = 0; i < visual.Query.From.Count; i++)
            {
                if (i > 0)
                {
                    // A visual query relates its entities through the model's relationships rather than by
                    // writing joins, so there is no ON predicate to render. The entities are listed as the
                    // tables this visual reads, which is exactly what consumption lineage needs from it.
                    builder.Append(", ");
                }

                var source = visual.Query.From[i];
                builder.Append(Quote(source.Entity)).Append(" AS ").Append(Quote(source.Alias));
            }
        }

        // Page filters first, then the visual's own: both apply, and both narrow the question.
        var predicates = new List<string>();
        foreach (var filter in pageFilters.Concat(visual.Filters))
        {
            if (filter.Condition is null)
            {
                continue;
            }

            // A filter carries its own FROM bindings, so its aliases resolve even when they are not the
            // visual query's. Where an alias is shared, the visual's binding is the same entity.
            var filterAliases = new Dictionary<string, string>(aliasEntities, StringComparer.Ordinal);
            foreach (var source in filter.From)
            {
                filterAliases[source.Alias] = source.Entity;
            }

            predicates.Add(Render(filter.Condition, filterAliases));
        }

        if (predicates.Count > 0)
        {
            builder.Append(" WHERE ");
            builder.Append(string.Join(" AND ", predicates));
        }

        if (visual.Query.OrderBy.Count > 0)
        {
            builder.Append(" ORDER BY ");
            for (var i = 0; i < visual.Query.OrderBy.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                var ordering = visual.Query.OrderBy[i];
                builder.Append(Render(ordering.Expression, aliasEntities));
                builder.Append(ordering.Descending ? " DESC" : " ASC");
            }
        }

        builder.Append(';');
        return builder.ToString();
    }

    private static string Render(QueryExpression expression, Dictionary<string, string> aliasEntities)
        => expression switch
        {
            ColumnExpression column
                => $"{RenderSource(column.SourceAlias, column.SourceEntity, aliasEntities)}.{Quote(column.Property)}",

            // A measure is model-computed business logic with no warehouse column behind it. It is rendered as
            // a qualified name so the reference is visible and the entity it belongs to still resolves; the DAX
            // that defines it lives in the part of the .pbix this extraction phase does not read.
            MeasureExpression measure
                => $"{RenderSource(measure.SourceAlias, measure.SourceEntity, aliasEntities)}.{Quote(measure.Property)}",

            // A hierarchy level is rendered as its LEVEL, which is the field the visual groups by and the name
            // a reader would look for ("Month", not "Fiscal").
            HierarchyLevelExpression hierarchy
                => $"{RenderSource(hierarchy.SourceAlias, hierarchy.SourceEntity, aliasEntities)}.{Quote(hierarchy.Level)}",

            AggregationExpression aggregation
                => $"{Function(aggregation.Function)}({Render(aggregation.Expression, aliasEntities)})",

            LiteralExpression literal => literal.Value,

            InExpression membership => RenderIn(membership, aliasEntities),

            NotExpression not => $"NOT ({Render(not.Expression, aliasEntities)})",

            LogicalExpression logical
                => $"({Render(logical.Left, aliasEntities)} {(logical.IsOr ? "OR" : "AND")} "
                   + $"{Render(logical.Right, aliasEntities)})",

            ComparisonExpression comparison
                => $"({Render(comparison.Left, aliasEntities)} {Operator(comparison.Operator)} "
                   + $"{Render(comparison.Right, aliasEntities)})",

            _ => throw new PbixUnsupportedExpressionException(
                $"the expression type '{expression.GetType().Name}' has no SQL rendering"),
        };

    private static string RenderIn(InExpression membership, Dictionary<string, string> aliasEntities)
    {
        // The single-column case is the common one and renders as a plain IN list. The multi-column case
        // renders as ORed tuples of ANDed equalities, which is the portable spelling of a row-value IN.
        if (membership.Expressions.Count == 1)
        {
            var target = Render(membership.Expressions[0], aliasEntities);
            var values = membership.Values
                .Select(tuple => tuple.Count == 1
                    ? Render(tuple[0], aliasEntities)
                    : throw new PbixUnsupportedExpressionException(
                        "a membership test whose value tuples do not match its expression count"));
            return $"{target} IN ({string.Join(", ", values)})";
        }

        var tuples = new List<string>();
        foreach (var tuple in membership.Values)
        {
            if (tuple.Count != membership.Expressions.Count)
            {
                throw new PbixUnsupportedExpressionException(
                    "a membership test whose value tuples do not match its expression count");
            }

            var equalities = membership.Expressions
                .Select((e, i) => $"{Render(e, aliasEntities)} = {Render(tuple[i], aliasEntities)}");
            tuples.Add($"({string.Join(" AND ", equalities)})");
        }

        return $"({string.Join(" OR ", tuples)})";
    }

    /// <summary>
    /// Renders the qualifier of a column/measure reference. PowerBI names either an alias from the query's FROM
    /// or an entity directly; an alias is kept as the alias (the FROM binds it), and a direct entity is used as
    /// written. A reference to an alias the query never bound is refused rather than guessed at, since guessing
    /// would attach the field to the wrong table and so to the wrong lineage node.
    /// </summary>
    private static string RenderSource(
        string? alias, string? entity, Dictionary<string, string> aliasEntities)
    {
        if (!string.IsNullOrEmpty(alias))
        {
            if (!aliasEntities.ContainsKey(alias))
            {
                throw new PbixUnsupportedExpressionException(
                    $"a reference to source alias '{alias}', which the query's FROM does not bind");
            }

            return Quote(alias);
        }

        if (!string.IsNullOrEmpty(entity))
        {
            return Quote(entity);
        }

        throw new PbixUnsupportedExpressionException("a reference with neither a source alias nor an entity");
    }

    private static string Function(AggregateFunction function)
        => function switch
        {
            AggregateFunction.Sum => "SUM",
            AggregateFunction.Average => "AVG",
            AggregateFunction.Count => "COUNT",
            AggregateFunction.Min => "MIN",
            AggregateFunction.Max => "MAX",
            // PowerBI's "count non-null" is SQL's plain COUNT(expression), which already ignores nulls.
            AggregateFunction.CountNonNull => "COUNT",
            _ => throw new PbixUnsupportedExpressionException(
                $"aggregate function '{function.ToString()}' has no SQL rendering"),
        };

    private static string Operator(ComparisonOperator comparison)
        => comparison switch
        {
            ComparisonOperator.Equal => "=",
            ComparisonOperator.NotEqual => "<>",
            ComparisonOperator.GreaterThan => ">",
            ComparisonOperator.GreaterThanOrEqual => ">=",
            ComparisonOperator.LessThan => "<",
            ComparisonOperator.LessThanOrEqual => "<=",
            _ => throw new PbixUnsupportedExpressionException(
                $"comparison operator '{comparison.ToString()}' has no SQL rendering"),
        };

    /// <summary>Bracket-quotes an identifier, escaping any closing bracket, so a model name with a space or a
    /// reserved word survives into parseable T-SQL.</summary>
    private static string Quote(string identifier)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]");
}
