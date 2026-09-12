using System.IO.Compression;
using System.Text;
using SqlFlow.PowerBi;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The visual layer of a Power BI report is the record of which business questions someone already decided
/// were worth asking: a chart titled "Sales Amount by Category" is a question with its shape already settled,
/// and the ROLE each field plays (what the chart is broken down BY versus what it plots) is the part of that
/// shape a flat column list from parsed SQL cannot express. These tests pin that extraction down, and pin down
/// the one thing that would make an extracted question wrong: dropping a filter, which would silently widen it.
/// <para>
/// The fixture is a synthetic <c>.pbix</c> built here rather than a checked-in binary. Its shape mirrors a real
/// Power BI Desktop file part-for-part (a zip whose <c>Report/Layout</c> is UTF-16LE JSON, with a visual's
/// <c>config</c> and <c>filters</c> held as JSON strings INSIDE that JSON), which is what the reader has to
/// cope with; the expected values below were confirmed against a genuine 8 MB AdventureWorks report.
/// </para>
/// </summary>
public sealed class PowerBiReportReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sqlflow-pbix-" + Guid.NewGuid().ToString("N")[..8]);

    public PowerBiReportReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>An area chart: a hierarchy level on the category axis, an aggregated column and a measure on Y.</summary>
    private const string AreaChartConfig = """
        {
          "singleVisual": {
            "visualType": "areaChart",
            "projections": {
              "Category": [{ "queryRef": "Date.Fiscal.Month" }],
              "Y": [{ "queryRef": "Sum(Sales.Sales Amount)" }, { "queryRef": "Sales.Sales Amount by Due Date" }]
            },
            "prototypeQuery": {
              "Version": 2,
              "From": [
                { "Name": "d", "Entity": "Date", "Type": 0 },
                { "Name": "s", "Entity": "Sales", "Type": 0 }
              ],
              "Select": [
                {
                  "Aggregation": {
                    "Expression": { "Column": { "Expression": { "SourceRef": { "Source": "s" } }, "Property": "Sales Amount" } },
                    "Function": 0
                  },
                  "Name": "Sum(Sales.Sales Amount)"
                },
                {
                  "Measure": { "Expression": { "SourceRef": { "Source": "s" } }, "Property": "Sales Amount by Due Date" },
                  "Name": "Sales.Sales Amount by Due Date"
                },
                {
                  "HierarchyLevel": {
                    "Expression": { "Hierarchy": { "Expression": { "SourceRef": { "Source": "d" } }, "Hierarchy": "Fiscal" } },
                    "Level": "Month"
                  },
                  "Name": "Date.Fiscal.Month"
                }
              ],
              "OrderBy": [
                {
                  "Direction": 1,
                  "Expression": {
                    "HierarchyLevel": {
                      "Expression": { "Hierarchy": { "Expression": { "SourceRef": { "Source": "d" } }, "Hierarchy": "Fiscal" } },
                      "Level": "Month"
                    }
                  }
                }
              ]
            },
            "vcObjects": {
              "title": [
                { "properties": { "text": { "expr": { "Literal": { "Value": "'Sales Amount by Order Date / Due Date'" } } } } }
              ]
            }
          }
        }
        """;

    /// <summary>A pivot table sorted descending by its measure: two row fields and one value field.</summary>
    private const string PivotTableConfig = """
        {
          "singleVisual": {
            "visualType": "pivotTable",
            "projections": {
              "Rows": [{ "queryRef": "Product.Category" }, { "queryRef": "Reseller.Business Type" }],
              "Values": [{ "queryRef": "Sum(Sales.Sales Amount)" }]
            },
            "prototypeQuery": {
              "Version": 2,
              "From": [
                { "Name": "p", "Entity": "Product", "Type": 0 },
                { "Name": "r", "Entity": "Reseller", "Type": 0 },
                { "Name": "s", "Entity": "Sales", "Type": 0 }
              ],
              "Select": [
                { "Column": { "Expression": { "SourceRef": { "Source": "p" } }, "Property": "Category" }, "Name": "Product.Category" },
                { "Column": { "Expression": { "SourceRef": { "Source": "r" } }, "Property": "Business Type" }, "Name": "Reseller.Business Type" },
                {
                  "Aggregation": {
                    "Expression": { "Column": { "Expression": { "SourceRef": { "Source": "s" } }, "Property": "Sales Amount" } },
                    "Function": 0
                  },
                  "Name": "Sum(Sales.Sales Amount)"
                }
              ],
              "OrderBy": [
                {
                  "Direction": 2,
                  "Expression": {
                    "Aggregation": {
                      "Expression": { "Column": { "Expression": { "SourceRef": { "Source": "s" } }, "Property": "Sales Amount" } },
                      "Function": 0
                    }
                  }
                }
              ]
            }
          }
        }
        """;

    /// <summary>
    /// The pivot table's filter, exactly as Power BI stores one: a <c>Not</c> over an <c>In</c>, written in the
    /// SAME expression language as the query it constrains. This is the shape that makes one translator enough.
    /// </summary>
    private const string PivotTableFilters = """
        [
          {
            "name": "Filterf32699ca5c7851734a77",
            "expression": { "Column": { "Expression": { "SourceRef": { "Entity": "Reseller" } }, "Property": "Business Type" } },
            "filter": {
              "Version": 2,
              "From": [{ "Name": "r", "Entity": "Reseller", "Type": 0 }],
              "Where": [
                {
                  "Condition": {
                    "Not": {
                      "Expression": {
                        "In": {
                          "Expressions": [
                            { "Column": { "Expression": { "SourceRef": { "Source": "r" } }, "Property": "Business Type" } }
                          ],
                          "Values": [[{ "Literal": { "Value": "'[Not Applicable]'" } }]]
                        }
                      }
                    }
                  }
                }
              ]
            },
            "type": "Categorical"
          }
        ]
        """;

    /// <summary>A textbox: it projects nothing, so it is decoration rather than a question.</summary>
    private const string TextboxConfig = """
        { "singleVisual": { "visualType": "textbox", "projections": {}, "objects": {} } }
        """;

    /// <summary>A shape: likewise decoration, and it carries no prototypeQuery at all.</summary>
    private const string BasicShapeConfig = """
        { "singleVisual": { "visualType": "basicShape", "projections": {} } }
        """;

    /// <summary>Builds a <c>.pbix</c>: a zip whose <c>Report/Layout</c> entry is UTF-16LE JSON.</summary>
    private string WritePbix(string layoutJson, string fileName = "report.pbix")
    {
        var path = Path.Combine(_root, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("Report/Layout");
        using var stream = entry.Open();
        // Power BI writes this part as UTF-16LE. Reading it as UTF-8 yields interleaved NULs, so the encoding
        // is part of the contract the reader has to honor and part of what this fixture exercises.
        stream.Write(Encoding.Unicode.GetBytes(layoutJson));
        return path;
    }

    /// <summary>Assembles a layout document from pre-serialized visual containers.</summary>
    private static string Layout(params string[] sections)
        => $$"""{ "id": 0, "sections": [{{string.Join(",", sections)}}] }""";

    private static string Section(
        string name, string displayName, int ordinal, string visuals, string filters = "[]")
        => $$"""
            {
              "id": {{ordinal}},
              "name": "{{name}}",
              "displayName": "{{displayName}}",
              "ordinal": {{ordinal}},
              "filters": {{Json(filters)}},
              "visualContainers": [{{visuals}}]
            }
            """;

    private static string Container(string config, string filters = "[]")
        => $$"""{ "x": 0, "y": 0, "z": 0, "width": 100, "height": 100, "config": {{Json(config)}}, "filters": {{Json(filters)}} }""";

    /// <summary>Embeds a JSON document AS A JSON STRING, which is how Power BI nests config and filters.</summary>
    private static string Json(string document)
        => System.Text.Json.JsonSerializer.Serialize(document);

    [Fact]
    public void Reader_ExtractsPagesVisualsAndFieldRoles()
    {
        var path = WritePbix(Layout(
            Section("s1", "Page 1", 1, string.Join(",",
                Container(AreaChartConfig),
                Container(PivotTableConfig, PivotTableFilters),
                Container(TextboxConfig),
                Container(BasicShapeConfig)))));

        var result = PbixReportReader.Read(path);

        Assert.Empty(result.Warnings);
        var page = Assert.Single(result.Layout.Pages);
        Assert.Equal("Page 1", page.DisplayName);
        Assert.Equal(1, page.Ordinal);

        // Four containers in, two visuals out: the textbox and the shape project no field, so they ask no
        // question and are deliberately not recorded as ones.
        Assert.Equal(2, page.Visuals.Count);
        Assert.DoesNotContain(page.Visuals, v => v.VisualType is "textbox" or "basicShape");

        var chart = page.Visuals[0];
        Assert.Equal("areaChart", chart.VisualType);
        // The title is stored as a single-quoted literal and must come back unquoted.
        Assert.Equal("Sales Amount by Order Date / Due Date", chart.Title);

        // The ROLE is the point: the hierarchy level is the axis, the measure and the aggregated column are
        // what gets plotted. Same three columns in a different arrangement would be a different question.
        Assert.Collection(
            chart.Fields,
            f =>
            {
                Assert.Equal("Category", f.Role);
                Assert.Equal("Date", f.TableName);
                // A hierarchy level resolves to the LEVEL, which is the field the chart groups by.
                Assert.Equal("Month", f.ColumnOrMeasure);
                Assert.False(f.IsMeasure);
            },
            f =>
            {
                Assert.Equal("Y", f.Role);
                Assert.Equal("Sales", f.TableName);
                // An aggregation is unwrapped to the field it aggregates.
                Assert.Equal("Sales Amount", f.ColumnOrMeasure);
                Assert.False(f.IsMeasure);
            },
            f =>
            {
                Assert.Equal("Y", f.Role);
                Assert.Equal("Sales", f.TableName);
                Assert.Equal("Sales Amount by Due Date", f.ColumnOrMeasure);
                // A measure is DAX-backed business logic, not a warehouse column, and is flagged as such.
                Assert.True(f.IsMeasure);
            });
    }

    [Fact]
    public void Reader_KeepsAVisualsFilterAsAParsedCondition()
    {
        var path = WritePbix(Layout(
            Section("s1", "Page 1", 1, Container(PivotTableConfig, PivotTableFilters))));

        var visual = Assert.Single(Assert.Single(PbixReportReader.Read(path).Layout.Pages).Visuals);

        // The filter is kept as a TREE, not as text: it has to become a real WHERE clause, and a blob would
        // leave something for a later step to re-parse and guess at.
        var filter = Assert.Single(visual.Filters);
        var not = Assert.IsType<NotExpression>(filter.Condition);
        var membership = Assert.IsType<InExpression>(not.Expression);
        var column = Assert.IsType<ColumnExpression>(Assert.Single(membership.Expressions));
        Assert.Equal("Business Type", column.Property);
        Assert.Equal("'[Not Applicable]'", Assert.IsType<LiteralExpression>(
            Assert.Single(Assert.Single(membership.Values))).Value);
    }

    [Fact]
    public void Translator_RendersTheVisualsQuery_WithItsFilterFoldedIntoWhere()
    {
        var path = WritePbix(Layout(
            Section("s1", "Page 1", 1, Container(PivotTableConfig, PivotTableFilters))));

        var page = Assert.Single(PbixReportReader.Read(path).Layout.Pages);
        var sql = VisualQueryTranslator.Translate(Assert.Single(page.Visuals), page.Filters);

        // The whole question in one statement: the fields, the tables, the filter that narrows it, and the sort.
        Assert.Equal(
            "SELECT [p].[Category] AS [Product.Category], [r].[Business Type] AS [Reseller.Business Type], "
            + "SUM([s].[Sales Amount]) AS [Sum(Sales.Sales Amount)] "
            + "FROM [Product] AS [p], [Reseller] AS [r], [Sales] AS [s] "
            + "WHERE NOT ([r].[Business Type] IN ('[Not Applicable]')) "
            + "ORDER BY SUM([s].[Sales Amount]) DESC;",
            sql);
    }

    [Fact]
    public void Translator_RendersAggregationsMeasuresAndHierarchyLevels()
    {
        var path = WritePbix(Layout(Section("s1", "Page 1", 1, Container(AreaChartConfig))));

        var page = Assert.Single(PbixReportReader.Read(path).Layout.Pages);
        var sql = VisualQueryTranslator.Translate(Assert.Single(page.Visuals), page.Filters);

        Assert.Equal(
            "SELECT SUM([s].[Sales Amount]) AS [Sum(Sales.Sales Amount)], "
            + "[s].[Sales Amount by Due Date] AS [Sales.Sales Amount by Due Date], "
            + "[d].[Month] AS [Date.Fiscal.Month] "
            + "FROM [Date] AS [d], [Sales] AS [s] "
            + "ORDER BY [d].[Month] ASC;",
            sql);
    }

    [Fact]
    public void Translator_FoldsPageLevelFiltersIntoEveryVisualOnThatPage()
    {
        // A page filter applies to every visual on the page, so it narrows each visual's question just as the
        // visual's own filter does. Leaving it out would record a question broader than the one on screen.
        var path = WritePbix(Layout(
            Section("s1", "Page 1", 1, Container(AreaChartConfig), PivotTableFilters)));

        var page = Assert.Single(PbixReportReader.Read(path).Layout.Pages);
        Assert.Single(page.Filters);

        var sql = VisualQueryTranslator.Translate(Assert.Single(page.Visuals), page.Filters);

        Assert.Contains("WHERE NOT ([r].[Business Type] IN ('[Not Applicable]'))", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RefusesAVisualWhoseFilterItCannotTranslate()
    {
        // The one failure that must never pass silently: an untranslatable filter would leave a query BROADER
        // than the visual's real question, which is exactly how a confirmed example ends up wrong. Refusing the
        // whole visual, loudly, is the honest outcome.
        const string unsupported = """
            [
              {
                "name": "Filter1",
                "filter": {
                  "Version": 2,
                  "From": [{ "Name": "r", "Entity": "Reseller", "Type": 0 }],
                  "Where": [{ "Condition": { "SomeFutureOperator": { "Left": {}, "Right": {} } } }]
                }
              }
            ]
            """;

        var path = WritePbix(Layout(
            Section("s1", "Page 1", 1, Container(PivotTableConfig, unsupported))));

        var result = PbixReportReader.Read(path);

        Assert.Empty(Assert.Single(result.Layout.Pages).Visuals);
        Assert.Contains(result.Warnings, w =>
            w.Contains("unsupported expression", StringComparison.Ordinal)
            && w.Contains("widen the question", StringComparison.Ordinal));
    }

    [Fact]
    public void Reader_ReportsAProjectionTheQueryDoesNotSelect()
    {
        // A projection naming a queryRef the query never selects cannot be resolved to a column, so it is
        // skipped with a warning rather than recorded as a field with no identity.
        const string danglingProjection = """
            {
              "singleVisual": {
                "visualType": "barChart",
                "projections": { "Y": [{ "queryRef": "Sum(Sales.Missing)" }, { "queryRef": "Product.Category" }] },
                "prototypeQuery": {
                  "Version": 2,
                  "From": [{ "Name": "p", "Entity": "Product", "Type": 0 }],
                  "Select": [
                    { "Column": { "Expression": { "SourceRef": { "Source": "p" } }, "Property": "Category" }, "Name": "Product.Category" }
                  ]
                }
              }
            }
            """;

        var path = WritePbix(Layout(Section("s1", "Page 1", 1, Container(danglingProjection))));

        var result = PbixReportReader.Read(path);

        var visual = Assert.Single(Assert.Single(result.Layout.Pages).Visuals);
        Assert.Equal("Product", Assert.Single(visual.Fields).TableName);
        Assert.Contains(result.Warnings, w =>
            w.Contains("Sum(Sales.Missing)", StringComparison.Ordinal)
            && w.Contains("which its query does not select", StringComparison.Ordinal));
    }

    [Fact]
    public void Reader_ReadsEveryPageInOrder()
    {
        var path = WritePbix(Layout(
            Section("s1", "Page 1", 1, Container(AreaChartConfig)),
            Section("s2", "Page 2", 2, Container(PivotTableConfig, PivotTableFilters)),
            Section("s3", "Sales Detail", 3, Container(AreaChartConfig))));

        var result = PbixReportReader.Read(path);

        Assert.Empty(result.Warnings);
        Assert.Equal(["Page 1", "Page 2", "Sales Detail"], result.Layout.Pages.Select(p => p.DisplayName));
        Assert.Equal([1, 2, 3], result.Layout.Pages.Select(p => p.Ordinal));
    }

    [Fact]
    public void Reader_RejectsAFileWithNoReportLayout()
    {
        // A .pbix always has this part; a zip without it is not a report, and saying so beats a null layout.
        var path = Path.Combine(_root, "empty.pbix");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("Version");
        }

        var ex = Assert.Throws<InvalidDataException>(() => PbixReportReader.Read(path));
        Assert.Contains("no 'Report/Layout' part", ex.Message, StringComparison.Ordinal);
    }
}
