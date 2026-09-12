using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SqlFlow.PowerBi;

/// <summary>What reading one <c>.pbix</c> produced: the report's visual layer, and anything the reader refused.</summary>
public sealed record PbixReadResult(ReportLayout Layout, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads the VISUAL LAYER out of a <c>.pbix</c> file: the pages, the visuals on them, the fields each visual
/// projects and the role each field plays, the query behind it, and its filters. A report is a record of the
/// business questions someone already decided were worth asking and already answered, and the visual layer is
/// where that record lives; this reader is what makes it machine-readable.
/// <para>
/// A <c>.pbix</c> is a zip. The <c>Report/Layout</c> part inside it is UTF-16LE JSON, needing no special
/// library, which is why the visual layer is tractable while the <c>DataModel</c> part (the DAX measures and
/// the declared model relationships) is not: that one is a compressed Analysis Services database and is out of
/// this reader's scope entirely.
/// </para>
/// <para>
/// Two of PowerBI's storage habits are normalized here. Several fields hold JSON *as a string* inside the
/// outer JSON (a visual's <c>config</c> and <c>filters</c>), so they are parsed a second time. And text
/// literals are stored already quoted (<c>'[Not Applicable]'</c>), which the literal handling preserves rather
/// than double-quoting.
/// </para>
/// <para>
/// The reader is strict about what it cannot represent. An expression node outside the supported set is
/// refused with a warning naming the visual, and that visual is dropped, because a half-translated query is
/// worse than an absent one: losing part of a filter would widen the question and losing part of a selection
/// would drop a field, in both cases producing a query that looks authoritative and is wrong.
/// </para>
/// </summary>
public static class PbixReportReader
{
    /// <summary>The zip entry holding the report's visual layer.</summary>
    private const string LayoutEntry = "Report/Layout";

    /// <summary>Reads the visual layer from a <c>.pbix</c> file on disk.</summary>
    public static PbixReadResult Read(string pbixPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pbixPath);

        using var archive = ZipFile.OpenRead(pbixPath);
        var entry = archive.GetEntry(LayoutEntry)
            ?? throw new InvalidDataException(
                $"'{pbixPath}' has no '{LayoutEntry}' part, so it carries no report layout. A .pbix saved by "
                + "Power BI Desktop always has one; a file without it is not a report (it may be a template).");

        using var stream = entry.Open();
        return ReadLayout(stream);
    }

    /// <summary>Reads the visual layer from an open <c>Report/Layout</c> stream.</summary>
    public static PbixReadResult ReadLayout(Stream layoutStream)
    {
        ArgumentNullException.ThrowIfNull(layoutStream);

        // The part is UTF-16LE. A BOM may or may not be present, and Encoding.Unicode honors one when it is.
        using var reader = new StreamReader(layoutStream, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
        var json = reader.ReadToEnd();

        var warnings = new List<string>();
        using var document = JsonDocument.Parse(json);

        var pages = new List<ReportPage>();
        if (document.RootElement.TryGetProperty("sections", out var sections)
            && sections.ValueKind == JsonValueKind.Array)
        {
            var ordinal = 0;
            foreach (var section in sections.EnumerateArray())
            {
                ordinal++;
                pages.Add(ReadPage(section, ordinal, warnings));
            }
        }
        else
        {
            warnings.Add(
                "the report layout declares no 'sections', so it has no pages; nothing was extracted from it.");
        }

        return new PbixReadResult(new ReportLayout { Pages = pages }, warnings);
    }

    private static ReportPage ReadPage(JsonElement section, int ordinal, List<string> warnings)
    {
        var name = Text(section, "name") ?? $"section{ordinal}";
        var displayName = Text(section, "displayName") ?? name;

        var pageFilters = ReadFilters(Text(section, "filters"));

        var visuals = new List<ReportVisual>();
        if (section.TryGetProperty("visualContainers", out var containers)
            && containers.ValueKind == JsonValueKind.Array)
        {
            var visualOrdinal = 0;
            foreach (var container in containers.EnumerateArray())
            {
                visualOrdinal++;
                var visual = ReadVisual(container, visualOrdinal, displayName, warnings);
                if (visual is not null)
                {
                    visuals.Add(visual);
                }
            }
        }

        return new ReportPage
        {
            Name = name,
            DisplayName = displayName,
            Ordinal = ordinal,
            Filters = pageFilters,
            Visuals = visuals,
        };
    }

    /// <summary>Reads one visual container, or null when it carries no question (decoration, or a refusal).</summary>
    private static ReportVisual? ReadVisual(
        JsonElement container, int ordinal, string pageName, List<string> warnings)
    {
        // The container's 'config' is JSON stored as a string inside the outer JSON.
        var configText = Text(container, "config");
        if (string.IsNullOrWhiteSpace(configText))
        {
            return null;
        }

        using var config = JsonDocument.Parse(configText);
        if (!config.RootElement.TryGetProperty("singleVisual", out var singleVisual))
        {
            // A grouped/container visual holds no query of its own; its children appear as their own entries.
            return null;
        }

        var visualType = Text(singleVisual, "visualType") ?? "unknown";
        var label = $"page '{pageName}' visual #{ordinal} ({visualType})";

        // A visual with no field projections is decoration: a shape, a textbox, an image. It asks no question,
        // so it is not a fact about what the business wants to know and is deliberately not recorded.
        if (!singleVisual.TryGetProperty("projections", out var projections)
            || projections.ValueKind != JsonValueKind.Object
            || !projections.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Array
                                                       && p.Value.GetArrayLength() > 0))
        {
            return null;
        }

        if (!singleVisual.TryGetProperty("prototypeQuery", out var prototypeQuery))
        {
            warnings.Add($"{label} projects fields but carries no query, so its fields cannot be resolved to "
                         + "tables; it was skipped.");
            return null;
        }

        VisualQuery query;
        try
        {
            query = ReadQuery(prototypeQuery);
        }
        catch (PbixUnsupportedExpressionException ex)
        {
            warnings.Add($"{label} uses a query expression the extractor does not support ({ex.Message}); the "
                         + "visual was skipped rather than recorded with an incomplete query.");
            return null;
        }

        // The selections are keyed by the name a projection's queryRef points at, which is how a field's ROLE
        // (what a chart is broken down BY versus what it plots) is attached to a concrete column or measure.
        var selectionsByName = new Dictionary<string, QuerySelection>(StringComparer.Ordinal);
        foreach (var selection in query.Select)
        {
            selectionsByName[selection.Name] = selection;
        }

        var sourcesByAlias = query.From.ToDictionary(f => f.Alias, f => f.Entity, StringComparer.Ordinal);

        var fields = new List<ReportField>();
        foreach (var role in projections.EnumerateObject())
        {
            if (role.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var projection in role.Value.EnumerateArray())
            {
                var queryRef = Text(projection, "queryRef");
                if (string.IsNullOrWhiteSpace(queryRef))
                {
                    continue;
                }

                if (!selectionsByName.TryGetValue(queryRef, out var selection))
                {
                    warnings.Add($"{label} projects '{queryRef}' in role '{role.Name}', which its query does not "
                                 + "select; that field was skipped.");
                    continue;
                }

                var resolved = ResolveField(selection.Expression, sourcesByAlias);
                if (resolved is null)
                {
                    warnings.Add($"{label} projects '{queryRef}' in role '{role.Name}', which resolves to no "
                                 + "column or measure; that field was skipped.");
                    continue;
                }

                fields.Add(new ReportField
                {
                    Role = role.Name,
                    QueryRef = queryRef,
                    TableName = resolved.Value.Table,
                    ColumnOrMeasure = resolved.Value.Name,
                    IsMeasure = resolved.Value.IsMeasure,
                });
            }
        }

        if (fields.Count == 0)
        {
            warnings.Add($"{label} projects fields but none resolved to a column or measure; it was skipped.");
            return null;
        }

        List<ReportFilter> filters;
        try
        {
            filters = ReadFilters(Text(container, "filters"));
        }
        catch (PbixUnsupportedExpressionException ex)
        {
            // A filter that cannot be translated must not be dropped quietly: without it the synthesized query
            // is BROADER than the question the visual actually asks, which is the one failure mode that would
            // make a confirmed example wrong. Refusing the whole visual is the honest outcome.
            warnings.Add($"{label} has a filter using an unsupported expression ({ex.Message}); the visual was "
                         + "skipped, because recording it without the filter would widen the question it asks.");
            return null;
        }

        return new ReportVisual
        {
            Ordinal = ordinal,
            VisualType = visualType,
            Title = ReadTitle(singleVisual),
            Fields = fields,
            Query = query,
            Filters = filters,
        };
    }

    /// <summary>
    /// Resolves a selected expression to the concrete table and column/measure it names, so a projection's
    /// ROLE can be attached to a real field. An aggregation is unwrapped to the field it aggregates (the
    /// question "sum of sales by month" is still about the Sales Amount column), and a hierarchy level
    /// resolves to its level, which is the field the visual actually groups by.
    /// <para>
    /// The alias is resolved through the query's own FROM bindings; a reference naming an entity directly uses
    /// that entity. Returns null when the expression names no field at all (a bare literal), which the caller
    /// reports and skips rather than recording a field with no identity.
    /// </para>
    /// </summary>
    private static (string Table, string Name, bool IsMeasure)? ResolveField(
        QueryExpression expression, Dictionary<string, string> sourcesByAlias)
    {
        switch (expression)
        {
            case AggregationExpression aggregation:
                return ResolveField(aggregation.Expression, sourcesByAlias);

            case ColumnExpression column:
            {
                var table = ResolveEntity(column.SourceAlias, column.SourceEntity, sourcesByAlias);
                return table is null ? null : (table, column.Property, false);
            }

            case MeasureExpression measure:
            {
                var table = ResolveEntity(measure.SourceAlias, measure.SourceEntity, sourcesByAlias);
                return table is null ? null : (table, measure.Property, true);
            }

            case HierarchyLevelExpression hierarchy:
            {
                var table = ResolveEntity(hierarchy.SourceAlias, hierarchy.SourceEntity, sourcesByAlias);
                return table is null ? null : (table, hierarchy.Level, false);
            }

            default:
                return null;
        }
    }

    /// <summary>Resolves a reference's qualifier to an entity name: an alias through the query's FROM, or an
    /// entity named directly. Null when neither resolves, so the caller can skip the field.</summary>
    private static string? ResolveEntity(
        string? alias, string? entity, Dictionary<string, string> sourcesByAlias)
    {
        if (!string.IsNullOrEmpty(alias) && sourcesByAlias.TryGetValue(alias, out var bound))
        {
            return bound;
        }

        return string.IsNullOrEmpty(entity) ? null : entity;
    }

    /// <summary>
    /// Reads a visual's authored title. PowerBI stores it as a literal expression nested under the visual's
    /// container objects (<c>vcObjects.title[].properties.text.expr.Literal.Value</c>), already single-quoted.
    /// </summary>
    private static string? ReadTitle(JsonElement singleVisual)
    {
        if (!singleVisual.TryGetProperty("vcObjects", out var vcObjects)
            || !vcObjects.TryGetProperty("title", out var titles)
            || titles.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var title in titles.EnumerateArray())
        {
            if (title.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("text", out var text)
                && text.TryGetProperty("expr", out var expr)
                && expr.TryGetProperty("Literal", out var literal))
            {
                var value = Text(literal, "Value");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return Unquote(value);
                }
            }
        }

        return null;
    }

    private static VisualQuery ReadQuery(JsonElement query)
    {
        var from = new List<QuerySource>();
        if (query.TryGetProperty("From", out var fromArray) && fromArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in fromArray.EnumerateArray())
            {
                var alias = Text(source, "Name");
                var entity = Text(source, "Entity");
                if (!string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(entity))
                {
                    from.Add(new QuerySource { Alias = alias, Entity = entity });
                }
            }
        }

        var select = new List<QuerySelection>();
        if (query.TryGetProperty("Select", out var selectArray) && selectArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var selection in selectArray.EnumerateArray())
            {
                var name = Text(selection, "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                select.Add(new QuerySelection { Name = name, Expression = ReadExpression(selection) });
            }
        }

        var orderBy = new List<QueryOrdering>();
        if (query.TryGetProperty("OrderBy", out var orderArray) && orderArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var ordering in orderArray.EnumerateArray())
            {
                if (!ordering.TryGetProperty("Expression", out var expression))
                {
                    continue;
                }

                // PowerBI encodes direction as 1 (ascending) or 2 (descending).
                var descending = ordering.TryGetProperty("Direction", out var direction)
                                 && direction.ValueKind == JsonValueKind.Number
                                 && direction.GetInt32() == 2;

                orderBy.Add(new QueryOrdering
                {
                    Expression = ReadExpression(expression),
                    Descending = descending,
                });
            }
        }

        return new VisualQuery { From = from, Select = select, OrderBy = orderBy };
    }

    /// <summary>
    /// Reads a filter array, stored as a JSON string inside the outer JSON. A filter's condition is kept as a
    /// tree so the translator can render it into the synthesized query's WHERE clause: the filter is part of
    /// the question, and a question missing its filter is a different question.
    /// </summary>
    private static List<ReportFilter> ReadFilters(string? filtersText)
    {
        var filters = new List<ReportFilter>();
        if (string.IsNullOrWhiteSpace(filtersText))
        {
            return filters;
        }

        using var document = JsonDocument.Parse(filtersText);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return filters;
        }

        foreach (var filter in document.RootElement.EnumerateArray())
        {
            var name = Text(filter, "name") ?? "filter";

            if (!filter.TryGetProperty("filter", out var body))
            {
                // A filter entry with no body constrains nothing (PowerBI keeps the field binding for the UI
                // even when no values are selected), so it contributes no predicate and is not an error.
                continue;
            }

            var from = new List<QuerySource>();
            if (body.TryGetProperty("From", out var fromArray) && fromArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in fromArray.EnumerateArray())
                {
                    var alias = Text(source, "Name");
                    var entity = Text(source, "Entity");
                    if (!string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(entity))
                    {
                        from.Add(new QuerySource { Alias = alias, Entity = entity });
                    }
                }
            }

            QueryExpression? condition = null;
            if (body.TryGetProperty("Where", out var whereArray) && whereArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var clause in whereArray.EnumerateArray())
                {
                    if (!clause.TryGetProperty("Condition", out var conditionElement))
                    {
                        continue;
                    }

                    var parsed = ReadExpression(conditionElement);
                    // Several Where entries are ANDed, exactly as separate predicates on one query would be.
                    condition = condition is null
                        ? parsed
                        : new LogicalExpression { Left = condition, Right = parsed, IsOr = false };
                }
            }

            if (condition is null)
            {
                continue;
            }

            filters.Add(new ReportFilter { Name = name, From = from, Condition = condition });
        }

        return filters;
    }

    /// <summary>
    /// Reads one expression node. The node's KIND is which property it carries, so the set is matched
    /// explicitly and anything else is refused: silently ignoring an unknown node would corrupt the meaning of
    /// the query or filter it appears in.
    /// </summary>
    private static QueryExpression ReadExpression(JsonElement node)
    {
        if (node.TryGetProperty("Column", out var column))
        {
            var (alias, entity) = ReadSourceRef(column);
            return new ColumnExpression
            {
                SourceAlias = alias,
                SourceEntity = entity,
                Property = Text(column, "Property")
                           ?? throw new PbixUnsupportedExpressionException("a column reference with no property"),
            };
        }

        if (node.TryGetProperty("Measure", out var measure))
        {
            var (alias, entity) = ReadSourceRef(measure);
            return new MeasureExpression
            {
                SourceAlias = alias,
                SourceEntity = entity,
                Property = Text(measure, "Property")
                           ?? throw new PbixUnsupportedExpressionException("a measure reference with no property"),
            };
        }

        if (node.TryGetProperty("HierarchyLevel", out var hierarchyLevel))
        {
            var level = Text(hierarchyLevel, "Level")
                        ?? throw new PbixUnsupportedExpressionException("a hierarchy level with no level name");

            if (!hierarchyLevel.TryGetProperty("Expression", out var hierarchyExpression)
                || !hierarchyExpression.TryGetProperty("Hierarchy", out var hierarchy))
            {
                throw new PbixUnsupportedExpressionException("a hierarchy level with no hierarchy");
            }

            var (alias, entity) = ReadSourceRef(hierarchy);
            return new HierarchyLevelExpression
            {
                SourceAlias = alias,
                SourceEntity = entity,
                Hierarchy = Text(hierarchy, "Hierarchy")
                            ?? throw new PbixUnsupportedExpressionException("a hierarchy with no name"),
                Level = level,
            };
        }

        if (node.TryGetProperty("Aggregation", out var aggregation))
        {
            if (!aggregation.TryGetProperty("Expression", out var aggregated))
            {
                throw new PbixUnsupportedExpressionException("an aggregation with no expression");
            }

            return new AggregationExpression
            {
                Expression = ReadExpression(aggregated),
                Function = MapAggregate(aggregation),
            };
        }

        if (node.TryGetProperty("Literal", out var literal))
        {
            return new LiteralExpression
            {
                Value = Text(literal, "Value")
                        ?? throw new PbixUnsupportedExpressionException("a literal with no value"),
            };
        }

        if (node.TryGetProperty("In", out var inNode))
        {
            var expressions = new List<QueryExpression>();
            if (inNode.TryGetProperty("Expressions", out var expressionArray)
                && expressionArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var expression in expressionArray.EnumerateArray())
                {
                    expressions.Add(ReadExpression(expression));
                }
            }

            var values = new List<IReadOnlyList<QueryExpression>>();
            if (inNode.TryGetProperty("Values", out var valueArray)
                && valueArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var tuple in valueArray.EnumerateArray())
                {
                    var parsedTuple = new List<QueryExpression>();
                    if (tuple.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var value in tuple.EnumerateArray())
                        {
                            parsedTuple.Add(ReadExpression(value));
                        }
                    }

                    values.Add(parsedTuple);
                }
            }

            if (expressions.Count == 0 || values.Count == 0)
            {
                throw new PbixUnsupportedExpressionException("a membership test with no expressions or no values");
            }

            return new InExpression { Expressions = expressions, Values = values };
        }

        if (node.TryGetProperty("Not", out var not))
        {
            if (!not.TryGetProperty("Expression", out var negated))
            {
                throw new PbixUnsupportedExpressionException("a negation with no expression");
            }

            return new NotExpression { Expression = ReadExpression(negated) };
        }

        if (node.TryGetProperty("And", out var and))
        {
            return ReadLogical(and, isOr: false);
        }

        if (node.TryGetProperty("Or", out var or))
        {
            return ReadLogical(or, isOr: true);
        }

        if (node.TryGetProperty("Comparison", out var comparison))
        {
            if (!comparison.TryGetProperty("Left", out var left)
                || !comparison.TryGetProperty("Right", out var right))
            {
                throw new PbixUnsupportedExpressionException("a comparison missing an operand");
            }

            return new ComparisonExpression
            {
                Left = ReadExpression(left),
                Right = ReadExpression(right),
                Operator = MapComparison(comparison),
            };
        }

        throw new PbixUnsupportedExpressionException(
            $"an unrecognized node ({string.Join(", ", node.EnumerateObject().Select(p => p.Name))})");
    }

    private static QueryExpression ReadLogical(JsonElement node, bool isOr)
    {
        if (!node.TryGetProperty("Left", out var left) || !node.TryGetProperty("Right", out var right))
        {
            throw new PbixUnsupportedExpressionException(
                $"a logical {(isOr ? "OR" : "AND")} missing an operand");
        }

        return new LogicalExpression
        {
            Left = ReadExpression(left),
            Right = ReadExpression(right),
            IsOr = isOr,
        };
    }

    /// <summary>Reads a <c>SourceRef</c>, which names either an alias from the query's FROM or an entity.</summary>
    private static (string? Alias, string? Entity) ReadSourceRef(JsonElement owner)
    {
        if (!owner.TryGetProperty("Expression", out var expression)
            || !expression.TryGetProperty("SourceRef", out var sourceRef))
        {
            return (null, null);
        }

        return (Text(sourceRef, "Source"), Text(sourceRef, "Entity"));
    }

    private static AggregateFunction MapAggregate(JsonElement aggregation)
    {
        if (!aggregation.TryGetProperty("Function", out var function)
            || function.ValueKind != JsonValueKind.Number)
        {
            throw new PbixUnsupportedExpressionException("an aggregation with no function code");
        }

        // PowerBI's numeric aggregate codes, as written into a visual's prototypeQuery.
        return function.GetInt32() switch
        {
            0 => AggregateFunction.Sum,
            1 => AggregateFunction.Average,
            2 => AggregateFunction.Count,
            3 => AggregateFunction.Min,
            4 => AggregateFunction.Max,
            5 => AggregateFunction.CountNonNull,
            var code => throw new PbixUnsupportedExpressionException($"aggregate function code {code}"),
        };
    }

    private static ComparisonOperator MapComparison(JsonElement comparison)
    {
        if (!comparison.TryGetProperty("ComparisonKind", out var kind) || kind.ValueKind != JsonValueKind.Number)
        {
            throw new PbixUnsupportedExpressionException("a comparison with no kind");
        }

        return kind.GetInt32() switch
        {
            0 => ComparisonOperator.Equal,
            1 => ComparisonOperator.GreaterThan,
            2 => ComparisonOperator.GreaterThanOrEqual,
            3 => ComparisonOperator.LessThan,
            4 => ComparisonOperator.LessThanOrEqual,
            5 => ComparisonOperator.NotEqual,
            var code => throw new PbixUnsupportedExpressionException($"comparison kind {code}"),
        };
    }

    /// <summary>Strips PowerBI's surrounding single quotes from a stored text literal.</summary>
    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1]
            : value;

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Raised when a PowerBI expression uses a construct the extractor cannot render as SQL. This is deliberately
/// an exception rather than a silent fallback: the caller turns it into a warning and DROPS the visual, because
/// a partially translated query would misstate the question the visual asks.
/// </summary>
public sealed class PbixUnsupportedExpressionException : Exception
{
    public PbixUnsupportedExpressionException()
        : base("The PowerBI query uses an expression the extractor does not support.")
    {
    }

    public PbixUnsupportedExpressionException(string message)
        : base(message)
    {
    }

    public PbixUnsupportedExpressionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
