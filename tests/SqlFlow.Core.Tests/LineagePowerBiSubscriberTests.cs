using System.IO.Compression;
using System.Text;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The collector side of Power BI extraction: a subscriber that declares <c>pbix:</c> gets the questions its
/// VISUALS ask turned into queries, and those queries are parsed on exactly the same path a hand-transcribed
/// subscriber query takes, so a chart's question becomes a real consumption edge on the same node the loading
/// flows write. The report's own structure (pages, visuals, and each field's ROLE) rides along beside the
/// queries, because the role is the one thing parsed SQL cannot recover: a column list cannot say which field
/// was the axis and which was the value.
/// </summary>
public sealed class LineagePowerBiSubscriberTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sqlflow-pbisub-" + Guid.NewGuid().ToString("N")[..8]);

    public LineagePowerBiSubscriberTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Ods = "${env:SQLFLOW_CONN_ODS}";

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private LineageReport Build()
        => LineageGraphBuilder.Build(new FlowSetCollector().Collect(_root), _root, [LineageTier.Declared], Utc);

    /// <summary>An ingestion writing the table the report's visual reads: the producing half of the graph.</summary>
    private static string Ingestion => string.Join('\n',
        "flowType: ing",
        "name: sales_02_ing",
        "connections:",
        "  pre: ${env:SQLFLOW_CONN_PRE}",
        $"  ods: {Ods}",
        "source:",
        "  server: pre",
        "  object: \"[PreDb].[pre].[v_Sales]\"",
        "target:",
        "  server: ods",
        "  object: \"[OdsDb].[arc].[Sales]\"",
        "load:",
        "  keyColumns: [id]") + '\n';

    /// <summary>
    /// One visual: a pivot table over the model entity <c>Sales</c>. A visual names MODEL entities, never
    /// warehouse objects, which is what the resolution assertions below pin down.
    /// </summary>
    private const string VisualConfig = """
        {
          "singleVisual": {
            "visualType": "pivotTable",
            "projections": {
              "Rows": [{ "queryRef": "Sales.region" }],
              "Values": [{ "queryRef": "Sum(Sales.amount)" }]
            },
            "prototypeQuery": {
              "Version": 2,
              "From": [{ "Name": "s", "Entity": "Sales", "Type": 0 }],
              "Select": [
                { "Column": { "Expression": { "SourceRef": { "Source": "s" } }, "Property": "region" }, "Name": "Sales.region" },
                {
                  "Aggregation": {
                    "Expression": { "Column": { "Expression": { "SourceRef": { "Source": "s" } }, "Property": "amount" } },
                    "Function": 0
                  },
                  "Name": "Sum(Sales.amount)"
                }
              ]
            },
            "vcObjects": {
              "title": [{ "properties": { "text": { "expr": { "Literal": { "Value": "'Revenue by Region'" } } } } }]
            }
          }
        }
        """;

    /// <summary>A textbox, which projects nothing: decoration, not a question.</summary>
    private const string TextboxConfig = """
        { "singleVisual": { "visualType": "textbox", "projections": {} } }
        """;

    private static string Json(string document) => System.Text.Json.JsonSerializer.Serialize(document);

    /// <summary>Writes a <c>.pbix</c>: a zip whose <c>Report/Layout</c> is UTF-16LE JSON.</summary>
    private void WritePbix(string relative)
    {
        var layout = $$"""
            {
              "id": 0,
              "sections": [
                {
                  "id": 1,
                  "name": "ReportSection1",
                  "displayName": "Revenue",
                  "ordinal": 1,
                  "filters": "[]",
                  "visualContainers": [
                    { "x": 0, "y": 0, "z": 0, "width": 100, "height": 100, "config": {{Json(VisualConfig)}}, "filters": "[]" },
                    { "x": 0, "y": 0, "z": 1, "width": 100, "height": 100, "config": {{Json(TextboxConfig)}}, "filters": "[]" }
                  ]
                }
              ]
            }
            """;

        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("Report/Layout");
        using var stream = entry.Open();
        stream.Write(Encoding.Unicode.GetBytes(layout));
    }

    private static string Subscribers(string pbix) => string.Join('\n',
        "connections:",
        $"  dwh: {Ods}",
        "subscribers:",
        "  Revenue_Report:",
        "    type: PowerBI",
        "    owner: analyse@kolumbus.no",
        "    description: Revenue dashboard",
        "    server: dwh",
        $"    pbix: {pbix}") + '\n';

    [Fact]
    public void DeclaredPbix_TurnsItsVisualsIntoConsumptionEdges()
    {
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/revenue.pbix");
        Write("subscribers.yaml", Subscribers("reports/revenue.pbix"));

        var report = Build();

        var subscriberKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Revenue_Report");

        // The visual's question becomes a real consumption edge, attributed to the subscriber and resolved
        // against its declared connection, on the same ScriptFactBuilder path a transcribed query takes.
        var read = Assert.Single(report.Edges, e =>
            e.Flow is null && e.ViaModule == subscriberKey && e.Relation == LineageRelation.Reads);

        // IMPORTANT, and a real limit of visual-layer-only extraction: a visual names the MODEL entity
        // ('Sales'), not the warehouse object, so the edge lands on a name-only node with NO database or
        // schema. It therefore does NOT unify with the [OdsDb].[arc].[Sales] node the ingestion writes.
        // Resolving a model entity to its physical table needs the Power Query / M source expressions, which
        // live in the .pbix's DataModel part and are out of this extraction's scope.
        Assert.Equal(NodeKey.For(Ods, null, null, "Sales"), read.ObjectKey);
        Assert.NotEqual(NodeKey.For(Ods, "OdsDb", "arc", "Sales"), read.ObjectKey);

        // The producing side is unaffected and still writes the fully-qualified node.
        Assert.Contains(report.Edges, e =>
            e.Flow == "sales_02_ing" && e.Relation == LineageRelation.Writes
            && e.ObjectKey == NodeKey.For(Ods, "OdsDb", "arc", "Sales"));
    }

    [Fact]
    public void DeclaredPbix_RecordsOneQueryPerVisual_NamedForIt()
    {
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/revenue.pbix");
        Write("subscribers.yaml", Subscribers("reports/revenue.pbix"));

        var subscriber = Assert.Single(Build().Subscribers);

        // One query, from the one visual that asks something; the textbox contributed nothing.
        var query = Assert.Single(subscriber.Queries);
        Assert.Equal("Revenue / Revenue by Region", query.Name);
        Assert.Contains("SUM([s].[amount])", query.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM [Sales] AS [s]", query.Sql, StringComparison.Ordinal);
        // The model entity resolves to a name-only node (see the edge test for why this is not the
        // database-qualified warehouse node).
        Assert.Equal(NodeKey.For(Ods, null, null, "Sales"), Assert.Single(query.ObjectKeys));
    }

    [Fact]
    public void DeclaredPbix_CarriesThePagesVisualsAndFieldRoles()
    {
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/revenue.pbix");
        Write("subscribers.yaml", Subscribers("reports/revenue.pbix"));

        var subscriber = Assert.Single(Build().Subscribers);

        var page = Assert.Single(subscriber.Pages);
        Assert.Equal("Revenue", page.DisplayName);
        Assert.Equal("ReportSection1", page.Name);
        Assert.Equal(1, page.Ordinal);

        var visual = Assert.Single(page.Visuals);
        Assert.Equal("pivotTable", visual.VisualType);
        Assert.Equal("Revenue by Region", visual.Title);
        // The structure points back at the SQL that carries its lineage, so the two halves stay joined.
        Assert.Equal("Revenue / Revenue by Region", visual.QueryName);

        // The ROLES: region is what the table is broken down BY, amount is what it reports. The same two
        // columns in swapped roles would be a different question, and the parsed SQL cannot tell them apart.
        Assert.Collection(
            visual.Fields,
            f =>
            {
                Assert.Equal("Rows", f.Role);
                Assert.Equal("Sales", f.TableName);
                Assert.Equal("region", f.ColumnOrMeasure);
                Assert.False(f.IsMeasure);
            },
            f =>
            {
                Assert.Equal("Values", f.Role);
                Assert.Equal("Sales", f.TableName);
                Assert.Equal("amount", f.ColumnOrMeasure);
                Assert.False(f.IsMeasure);
            });
    }

    [Fact]
    public void HandAuthoredSubscriber_KeepsWorking_AndCarriesNoPages()
    {
        // The declaration is additive: a subscriber with no 'pbix:' behaves exactly as before.
        Write("10_ing.yaml", Ingestion);
        Write("subscribers.yaml", string.Join('\n',
            "connections:",
            $"  dwh: {Ods}",
            "subscribers:",
            "  Legacy_Report:",
            "    type: PowerBI",
            "    server: dwh",
            "    queries:",
            "      - name: Totals",
            "        sql: SELECT * FROM [OdsDb].[arc].[Sales];") + '\n');

        var subscriber = Assert.Single(Build().Subscribers);

        Assert.Equal("Totals", Assert.Single(subscriber.Queries).Name);
        Assert.Empty(subscriber.Pages);
    }

    [Fact]
    public void DeclaredPbix_AlongsideHandAuthoredQueries_KeepsBoth()
    {
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/revenue.pbix");
        Write("subscribers.yaml", string.Join('\n',
            "connections:",
            $"  dwh: {Ods}",
            "subscribers:",
            "  Revenue_Report:",
            "    type: PowerBI",
            "    server: dwh",
            "    pbix: reports/revenue.pbix",
            "    queries:",
            "      - name: Hand written",
            "        sql: SELECT * FROM [OdsDb].[arc].[Sales];") + '\n');

        var subscriber = Assert.Single(Build().Subscribers);

        // A transcribed query someone already verified is not discarded because the file can now be read.
        Assert.Equal(
            ["Hand written", "Revenue / Revenue by Region"],
            subscriber.Queries.Select(q => q.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void MissingPbix_IsWarned_AndLeavesTheRestOfLineageIntact()
    {
        Write("10_ing.yaml", Ingestion);
        Write("subscribers.yaml", Subscribers("reports/absent.pbix"));

        var report = Build();

        // The subscriber still exists as a consumer node; only its extraction failed, and it said so.
        var subscriber = Assert.Single(report.Subscribers);
        Assert.Empty(subscriber.Pages);
        Assert.Contains(report.Warnings, w =>
            w.Contains("reports/absent.pbix", StringComparison.Ordinal)
            && w.Contains("does not exist", StringComparison.Ordinal));

        // The producing side of the graph is unaffected by one unreadable report.
        Assert.Contains(report.Edges, e =>
            e.Flow == "sales_02_ing" && e.Relation == LineageRelation.Writes);
    }

    [Fact]
    public void UnreadablePbix_IsWarned_RatherThanThrowing()
    {
        // A file that is not a report at all (no Report/Layout part) must degrade to a warning, exactly as an
        // unparseable flow document does, rather than aborting the estate's collection.
        Write("10_ing.yaml", Ingestion);
        var path = Path.Combine(_root, "reports", "broken.pbix");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("Version");
        }

        Write("subscribers.yaml", Subscribers("reports/broken.pbix"));

        var report = Build();

        Assert.Empty(Assert.Single(report.Subscribers).Pages);
        Assert.Contains(report.Warnings, w =>
            w.Contains("could not be read", StringComparison.Ordinal)
            && w.Contains("broken.pbix", StringComparison.Ordinal));
    }
}
