using System.Text.Json;

namespace PbiVisualExport.Tests;

public class ReportDefinitionTests
{
    [Theory]
    [InlineData(new[] { "definition/report.json", "definition.pbir" }, "PBIR")]
    [InlineData(new[] { "Definition/Report.json" }, "PBIR")]
    [InlineData(new[] { "report.json", "definition.pbir" }, "PBIR-Legacy")]
    [InlineData(new[] { "definition.pbir" }, "Unknown")]
    public void DetectFormat(string[] paths, string expected) =>
        Assert.Equal(expected, ReportDefinition.DetectFormat(paths));

    [Fact]
    public void StoreSales_ReportMeasures_AreParsedWithDependencies()
    {
        var report = Fixtures.Report(Fixtures.StoreSales);
        Assert.True(report.IsPbir);
        Assert.Equal(new[] { "Sales per Store", "Sales per Store (K)" }, report.Measures.Select(m => m.Name));

        var k = report.FindMeasure("sales per store (k)")!;
        Assert.Equal("Sales", k.Entity);
        Assert.Equal("\\$#,0.0\"K\"", k.FormatString);
        Assert.Equal(new[] { "Sales per Store" }, k.DependsOn);
        // Model measures in references (no schema) aren't report-measure dependencies.
        Assert.Empty(report.FindMeasure("Sales per Store")!.DependsOn);
    }

    [Fact]
    public void WithDependencies_OrdersDependenciesFirst_EachOnce()
    {
        var report = Fixtures.Report(Fixtures.StoreSales);
        var k = report.FindMeasure("Sales per Store (K)")!;
        var perStore = report.FindMeasure("Sales per Store")!;

        Assert.Equal(new[] { perStore, k }, report.WithDependencies(new[] { k }));
        Assert.Equal(new[] { perStore, k }, report.WithDependencies(new[] { perStore, k, k }));
    }

    [Fact]
    public void ReportExtensions_DependenciesFromExpressionTokens_WhenReferencesAreMissing()
    {
        var measures = ReportDefinition.ParseReportExtensions("""
            { "entities": [ { "name": "Sales", "measures": [
                { "name": "A", "expression": "[B] + [Model Measure] + [C]]x]" },
                { "name": "B", "expression": "1" },
                { "name": "C]x", "expression": "2" },
                { "name": "Self", "expression": "[Self] + 1" },
                { "name": "", "expression": "3" },
                { "name": "NoExpression" } ] } ] }
            """);
        Assert.Equal(new[] { "A", "B", "C]x", "Self" }, measures.Select(m => m.Name));
        Assert.Equal(new[] { "B", "C]x" }, measures[0].DependsOn);
        Assert.Empty(measures[3].DependsOn);
    }

    [Fact]
    public void FindVisual_ByPageAndVisualName_CaseInsensitive()
    {
        var report = Fixtures.Report(Fixtures.StoreSales);
        var v = report.FindVisual("page3", "RMTABLE");
        Assert.NotNull(v);
        Assert.Equal("rmTable", v!.Name);
        Assert.Equal("Page3", v.PageName);
        Assert.Null(report.FindVisual("Page1", "rmTable"));
    }

    [Fact]
    public void Visuals_UsePageNameFromPageJson_SkipGroups_AndReportUnreadableParts()
    {
        var report = Samples.Pbir(
            ("pages/ReportSection1/page.json", """{ "name": "abc123", "displayName": "Overview" }"""),
            ("pages/ReportSection1/visuals/v1/visual.json", Samples.VisualJson("visual1", new[] { Samples.ColumnExpr("Sales", "Region") })),
            ("pages/ReportSection1/visuals/group1/visual.json", """{ "name": "group1", "visualGroup": { "displayName": "Group" } }"""),
            ("pages/ReportSection1/visuals/broken/visual.json", "{ not json"),
            ("pages/Other/visuals/v2/visual.json", Samples.VisualJson("v2", new[] { Samples.MeasureExpr("Sales", "Total") })));

        Assert.NotNull(report.FindVisual("abc123", "visual1"));
        Assert.Null(report.FindVisual("ReportSection1", "visual1"));
        Assert.Null(report.FindVisual("abc123", "group1"));
        Assert.NotNull(report.FindVisual("Other", "v2")); // no page.json: the folder name
        Assert.Equal(2, report.Visuals.Count());
        var error = Assert.Single(report.VisualErrors);
        Assert.StartsWith("definition/pages/ReportSection1/visuals/broken/visual.json:", error);
    }

    [Fact]
    public void Legacy_HasNoMeasuresOrVisuals()
    {
        var report = Samples.Legacy();
        Assert.Equal(ReportDefinition.PbirLegacy, report.Format);
        Assert.False(report.IsPbir);
        Assert.Empty(report.Measures);
        Assert.Empty(report.Visuals);
    }

    [Fact]
    public void VisualDefinition_ReadsFieldsFieldParametersAndSavedSort()
    {
        var json = $$"""
            { "name": "t1", "visual": { "visualType": "tableEx", "query": {
                "queryState": {
                  "Values": { "projections": [ { "field": {{Samples.ColumnExpr("Sales", "Region")}} }, { "field": {{Samples.MeasureExpr("Sales", "Total")}} } ] },
                  "Tooltips": { "projections": [ { "field": {{Samples.AggExpr("Sales", "Amount", 1)}} } ] },
                  "Rows": { "fieldParameters": [ { "parameterExpr": {{Samples.ColumnExpr("Fields", "Fields")}} } ] } },
                "sortDefinition": { "sort": [ { "field": {{Samples.MeasureExpr("Sales", "Count")}}, "direction": "Descending" } ] } } } }
            """;
        var v = VisualDefinition.Parse("p", "folder", json)!;
        Assert.Equal("t1", v.Name);
        Assert.Equal("tableEx", v.VisualType);
        Assert.Equal(new[]
        {
            new VisualFieldRef(VisualFieldKind.Column, "Sales", "Region"),
            new VisualFieldRef(VisualFieldKind.Measure, "Sales", "Total"),
            new VisualFieldRef(VisualFieldKind.Aggregation, "Sales", "Amount", "Avg"),
            new VisualFieldRef(VisualFieldKind.Column, "Fields", "Fields")
        }, v.Fields);
        Assert.Equal(new[] { new VisualFieldRef(VisualFieldKind.Measure, "Sales", "Count") }, v.SortFields);
        Assert.True(v.CanSortBy(new ExportSort { Table = "Sales", Measure = "count" }));
        Assert.False(v.Shows(new ExportField { Table = "Sales", Measure = "Count" }));
    }

    [Theory]
    [InlineData(0, "Sum")]
    [InlineData(1, "Avg")]
    [InlineData(2, "DistinctCount")]
    [InlineData(3, "Min")]
    [InlineData(4, "Max")]
    [InlineData(5, "Count")]
    [InlineData(6, "Median")]
    [InlineData(7, "StandardDeviation")]
    [InlineData(8, "Variance")]
    [InlineData(9, null)]
    public void ParseField_AggregationFunctionCodes(int code, string? expected)
    {
        var r = VisualDefinition.ParseField(Element(Samples.AggExpr("Sales", "Amount", code)));
        Assert.Equal(expected, r?.Aggregation);
    }

    [Fact]
    public void ParseField_UnsupportedExpressions_AreNull()
    {
        Assert.Null(VisualDefinition.ParseField(Element("""{"HierarchyLevel":{"Expression":{"Hierarchy":{}},"Level":"Year"}}""")));
        Assert.Null(VisualDefinition.ParseField(Element("""{"Column":{"Property":"NoSource"}}""")));
        Assert.Null(VisualDefinition.ParseField(Element("\"text\"")));
        Assert.Equal("Sum", VisualDefinition.ParseField(Element(
            """{"Aggregation":{"Expression":{"Column":{"Expression":{"SourceRef":{"Entity":"S"}},"Property":"P"}},"Function":"Sum"}}"""))!.Aggregation);
    }

    private static JsonElement Element(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
