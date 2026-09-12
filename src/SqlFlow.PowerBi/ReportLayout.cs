namespace SqlFlow.PowerBi;

/// <summary>
/// A PowerBI report's visual layer as the extractor models it: the pages a person flips through and, on each,
/// the visuals that carry a business question. This is the shape read out of a <c>.pbix</c>'s
/// <c>Report/Layout</c> part, normalized away from PowerBI's on-disk quirks (nested JSON-in-JSON strings, the
/// <c>sections</c>/<c>visualContainers</c> naming) into names that say what the things are.
/// </summary>
public sealed record ReportLayout
{
    public required IReadOnlyList<ReportPage> Pages { get; init; }
}

/// <summary>One page of a report, in the order PowerBI lists it.</summary>
public sealed record ReportPage
{
    /// <summary>The page's internal identifier (PowerBI's <c>section.name</c>), a generated string.</summary>
    public required string Name { get; init; }

    /// <summary>The page's title as a person sees it on the tab (PowerBI's <c>section.displayName</c>).</summary>
    public required string DisplayName { get; init; }

    /// <summary>The page's position within the report, 1-based.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Filters declared on the page itself, applying to every visual on it.</summary>
    public required IReadOnlyList<ReportFilter> Filters { get; init; }

    public required IReadOnlyList<ReportVisual> Visuals { get; init; }
}

/// <summary>
/// One visual on a page: what kind of chart it is, which fields it projects into which role, and the query
/// PowerBI runs to fill it. A visual that projects no field at all (a shape, a textbox) is decoration and is
/// dropped by the reader rather than represented here, so every instance of this record is a real question.
/// </summary>
public sealed record ReportVisual
{
    /// <summary>The visual's position within its page, 1-based.</summary>
    public required int Ordinal { get; init; }

    /// <summary>PowerBI's visual type: <c>areaChart</c>, <c>pivotTable</c>, <c>slicer</c>, and so on.</summary>
    public required string VisualType { get; init; }

    /// <summary>The visual's authored title, when it has one.</summary>
    public string? Title { get; init; }

    /// <summary>The fields and measures the visual projects, each tagged with the role it plays.</summary>
    public required IReadOnlyList<ReportField> Fields { get; init; }

    /// <summary>The query PowerBI runs for this visual, as a parsed expression tree.</summary>
    public required VisualQuery Query { get; init; }

    /// <summary>Filters declared on this visual alone.</summary>
    public required IReadOnlyList<ReportFilter> Filters { get; init; }
}

/// <summary>
/// One field or measure a visual projects, and THE ROLE IT PLAYS: the distinction between "sales by month" and
/// "months by sales". The role is PowerBI's own projection bucket (<c>Category</c>/<c>Y</c> for a chart,
/// <c>Rows</c>/<c>Values</c> for a pivot table, <c>Size</c> for a map), kept verbatim because it is the report
/// author's own statement of the question's shape, and it is precisely what a flat column list from parsed SQL
/// cannot express.
/// </summary>
public sealed record ReportField
{
    public required string Role { get; init; }

    /// <summary>The table or entity the field belongs to, as the query named it.</summary>
    public required string TableName { get; init; }

    /// <summary>The column or measure name within <see cref="TableName"/>.</summary>
    public required string ColumnOrMeasure { get; init; }

    /// <summary>True when this is a measure (DAX-backed business logic) rather than a plain column.</summary>
    public required bool IsMeasure { get; init; }
}

/// <summary>
/// A visual's query: the tables it reads, the expressions it selects, and its sort. PowerBI stores this as its
/// own little expression tree (<c>prototypeQuery</c>), which is what makes a visual machine-readable rather
/// than a picture; the translator renders it as T-SQL so the estate's existing lineage parser can read it.
/// </summary>
public sealed record VisualQuery
{
    /// <summary>The query's sources: an alias (<c>s</c>) bound to an entity (<c>Sales</c>).</summary>
    public required IReadOnlyList<QuerySource> From { get; init; }

    public required IReadOnlyList<QuerySelection> Select { get; init; }

    public required IReadOnlyList<QueryOrdering> OrderBy { get; init; }
}

/// <summary>One <c>FROM</c> entry of a visual query: the alias its expressions use, and the entity it names.</summary>
public sealed record QuerySource
{
    /// <summary>The alias expressions refer to (PowerBI's <c>From[].Name</c>).</summary>
    public required string Alias { get; init; }

    /// <summary>The table/entity the alias is bound to (PowerBI's <c>From[].Entity</c>).</summary>
    public required string Entity { get; init; }
}

/// <summary>One selected expression of a visual query, with the output name PowerBI gave it.</summary>
public sealed record QuerySelection
{
    /// <summary>The selection's name, which is also the <c>queryRef</c> a projection points at.</summary>
    public required string Name { get; init; }

    public required QueryExpression Expression { get; init; }
}

/// <summary>One sort of a visual query.</summary>
public sealed record QueryOrdering
{
    public required QueryExpression Expression { get; init; }

    /// <summary>True for descending. PowerBI encodes direction as 1 (ascending) or 2 (descending).</summary>
    public required bool Descending { get; init; }
}

/// <summary>
/// One filter applied to a visual or a page. The condition is kept as a parsed tree rather than as text,
/// because a filter is part of the question the visual answers (a chart filtered to one fiscal year asks a
/// different question from the unfiltered chart) and must end up in the WHERE clause of the query the
/// extractor synthesizes. Its sources are carried alongside, since a filter names its own aliases.
/// </summary>
public sealed record ReportFilter
{
    /// <summary>PowerBI's filter name, a generated identifier; useful only for diagnostics.</summary>
    public required string Name { get; init; }

    /// <summary>The filter's own <c>FROM</c> bindings, which its condition's aliases refer to.</summary>
    public required IReadOnlyList<QuerySource> From { get; init; }

    /// <summary>The filter's condition, or null when PowerBI stored a filter with no condition (an
    /// interactively-cleared filter), in which case it constrains nothing and contributes no predicate.</summary>
    public QueryExpression? Condition { get; init; }
}
