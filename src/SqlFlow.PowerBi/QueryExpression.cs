namespace SqlFlow.PowerBi;

/// <summary>
/// One node of a PowerBI query expression. Visual queries (<c>prototypeQuery</c>) and filter conditions are
/// built from the SAME node vocabulary, which is why one tree type and one translator serve both: a filter is
/// not a different kind of thing from the query it constrains, it is more of the same expression language.
/// <para>
/// The set is closed on purpose. PowerBI's expression language is larger than this, but an unrecognized node
/// cannot be silently dropped: dropping part of a filter would widen the question (a query that returns more
/// rows than the visual shows) and dropping part of a selection would lose a field. The reader therefore
/// refuses what it cannot represent, and the refusal is reported as a warning naming the visual, rather than
/// producing a query that is quietly wrong.
/// </para>
/// </summary>
public abstract record QueryExpression;

/// <summary>A column reference: <c>Sales.Sales Amount</c>, via the alias its source was bound to.</summary>
public sealed record ColumnExpression : QueryExpression
{
    /// <summary>The source alias the column belongs to, or null when the reference named an entity directly
    /// (PowerBI allows <c>SourceRef.Entity</c> in place of an alias, notably inside filters).</summary>
    public string? SourceAlias { get; init; }

    /// <summary>The entity named directly, when the reference did not go through an alias.</summary>
    public string? SourceEntity { get; init; }

    public required string Property { get; init; }
}

/// <summary>A measure reference: DAX-backed business logic, named like a column but computed by the model.</summary>
public sealed record MeasureExpression : QueryExpression
{
    public string? SourceAlias { get; init; }

    public string? SourceEntity { get; init; }

    public required string Property { get; init; }
}

/// <summary>
/// A level of a date/other hierarchy: <c>Date.Fiscal.Month</c>. The hierarchy is a model construct with no
/// single warehouse column behind it, so the translator renders the LEVEL as the column: it is the field the
/// visual actually groups by, and the name a reader would look for.
/// </summary>
public sealed record HierarchyLevelExpression : QueryExpression
{
    public string? SourceAlias { get; init; }

    public string? SourceEntity { get; init; }

    public required string Hierarchy { get; init; }

    public required string Level { get; init; }
}

/// <summary>An aggregation over an expression: PowerBI's implicit <c>Sum</c>, <c>Average</c>, and so on.</summary>
public sealed record AggregationExpression : QueryExpression
{
    public required QueryExpression Expression { get; init; }

    /// <summary>The aggregate function, already mapped from PowerBI's numeric function code.</summary>
    public required AggregateFunction Function { get; init; }
}

/// <summary>The aggregate functions PowerBI encodes numerically in a visual query.</summary>
public enum AggregateFunction
{
    Sum,
    Average,
    Count,
    Min,
    Max,
    CountNonNull,
}

/// <summary>A literal value, carried as PowerBI wrote it (already including its quoting for text).</summary>
public sealed record LiteralExpression : QueryExpression
{
    public required string Value { get; init; }
}

/// <summary>A membership test: one or more expressions against a set of value tuples.</summary>
public sealed record InExpression : QueryExpression
{
    public required IReadOnlyList<QueryExpression> Expressions { get; init; }

    /// <summary>The permitted value tuples; each inner list lines up with <see cref="Expressions"/>.</summary>
    public required IReadOnlyList<IReadOnlyList<QueryExpression>> Values { get; init; }
}

/// <summary>A negation.</summary>
public sealed record NotExpression : QueryExpression
{
    public required QueryExpression Expression { get; init; }
}

/// <summary>A conjunction or disjunction of two conditions.</summary>
public sealed record LogicalExpression : QueryExpression
{
    public required QueryExpression Left { get; init; }

    public required QueryExpression Right { get; init; }

    public required bool IsOr { get; init; }
}

/// <summary>A comparison between two expressions.</summary>
public sealed record ComparisonExpression : QueryExpression
{
    public required QueryExpression Left { get; init; }

    public required QueryExpression Right { get; init; }

    public required ComparisonOperator Operator { get; init; }
}

/// <summary>The comparison operators PowerBI encodes numerically in a condition.</summary>
public enum ComparisonOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
}
