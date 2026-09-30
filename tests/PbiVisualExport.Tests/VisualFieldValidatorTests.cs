using Microsoft.AspNetCore.Http;

namespace PbiVisualExport.Tests;

public class VisualFieldValidatorTests
{
    private const string Report = "LargeExportReport";

    private static VisualExportRequest Summary1() => Fixtures.RequestFromVisual(Report, "Page2", "summary1");

    private static FieldValidationResult Validate(VisualExportRequest r, FieldValidationMode mode = FieldValidationMode.Strict) =>
        VisualFieldValidator.Validate(r, Fixtures.Report(Report), mode);

    [Fact]
    public void SavedVisualFields_AreAllowed()
    {
        var result = Validate(Summary1());
        Assert.Null(result.Error);
        Assert.True(result.Checked);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SavedHierarchyLevels_AreAllowed_WithTheSdksAggregationName()
    {
        // table4b: levels of two hierarchies, "Count of Line" and a saved sort on a level.
        var r = Fixtures.RequestFromVisual(Report, "Page4", "table4b");
        Assert.Equal(4, r.Fields.Count(f => f.IsHierarchyLevel));
        Assert.True(r.Sort.Single().IsHierarchyLevel);
        Assert.Equal("CountNonNull", r.Fields.Single(f => f.Aggregation is not null).Aggregation); // as getDataFields reports it
        var result = Validate(r);
        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
        Assert.Single(result.Request.Sort);
    }

    [Fact]
    public void Aggregations_AreNamedAsTheSdkNamesThem()
    {
        // Code 2 is Count (Distinct), which getDataFields calls "Count"; code 5 is Count ("CountNonNull").
        var report = Samples.Pbir(("pages/p/visuals/v/visual.json", Samples.VisualJson("v",
            new[] { Samples.ColumnExpr("Sales", "Region"), Samples.AggExpr("Sales", "Amount", 2) })));
        FieldValidationResult Check(string aggregation)
        {
            var r = Samples.Request(Samples.Column("Sales", "Region"),
                new ExportField { Table = "Sales", Column = "Amount", Aggregation = aggregation });
            r.PageName = "p";
            r.VisualName = "v";
            return VisualFieldValidator.Validate(r, report, FieldValidationMode.Strict);
        }
        Assert.Null(Check("Count").Error);
        Assert.Null(Check("DistinctCount").Error);
        Assert.Equal(StatusCodes.Status403Forbidden, Check("CountNonNull").StatusCode);
    }

    [Fact]
    public void Off_SkipsTheCheck()
    {
        var r = Summary1();
        r.Fields.Add(Samples.Column("Orders", "Notes"));
        var result = Validate(r, FieldValidationMode.Off);
        Assert.Null(result.Error);
        Assert.False(result.Checked);
        Assert.Same(r, result.Request);
    }

    [Fact]
    public void Legacy_IsRefusedUnderStrict_AndUncheckedUnderPbirOnly()
    {
        var strict = VisualFieldValidator.Validate(Summary1(), Samples.Legacy(), FieldValidationMode.Strict);
        Assert.Equal(StatusCodes.Status403Forbidden, strict.StatusCode);
        Assert.Contains("PBIR-Legacy", strict.Error);
        Assert.False(strict.Stale);

        var pbirOnly = VisualFieldValidator.Validate(Summary1(), Samples.Legacy(), FieldValidationMode.PbirOnly);
        Assert.Null(pbirOnly.Error);
        Assert.False(pbirOnly.Checked);
    }

    [Theory]
    [InlineData(null, "summary1")]
    [InlineData("Page2", " ")]
    public void MissingPageOrVisual_IsBadRequest(string? page, string? visual)
    {
        var r = Summary1();
        r.PageName = page;
        r.VisualName = visual;
        var result = Validate(r);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("pageName and visualName are required", result.Error);
    }

    [Fact]
    public void UnknownVisual_IsForbiddenAndStale()
    {
        var r = Summary1();
        r.VisualName = "notSaved";
        var result = Validate(r);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.True(result.Stale);
        Assert.Contains("(notSaved) on page Page2 is not in the published definition", result.Error);
    }

    [Fact]
    public void ExtraColumn_IsForbiddenAndStale()
    {
        var r = Summary1();
        r.Fields.Add(Samples.Column("Orders", "Customer Name"));
        r.Fields.Add(Samples.Measure("Orders", "Order Count"));
        var result = Validate(r);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.True(result.Stale);
        Assert.Contains("'Orders'[Customer Name], [Order Count]", result.Error);
    }

    [Fact]
    public void DifferentAggregationOfAVisualColumn_IsForbidden()
    {
        var r = Summary1();
        r.Fields.Single(f => f.Column == "Unit Price").Aggregation = "Sum";
        var result = Validate(r);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Contains("Sum('Orders'[Unit Price])", result.Error);
    }

    [Fact]
    public void AggregationAliasesAndMeasureNameCase_Match()
    {
        var r = Summary1();
        r.Fields.Single(f => f.Column == "Unit Price").Aggregation = "Average";
        r.Fields.Single(f => f.Measure == "Total Sales").Measure = "TOTAL SALES";
        r.Fields.Single(f => f.Column == "Region").Table = "orders";
        Assert.Null(Validate(r).Error);
    }

    [Fact]
    public void SortOutsideTheVisual_IsDroppedWithAWarning()
    {
        var r = Summary1();
        r.Sort.Add(new ExportSort { Table = "Orders", Measure = "Total Sales", Direction = "Descending" });
        r.Sort.Add(new ExportSort { Table = "Orders", Column = "Row Id" });
        var result = Validate(r);

        Assert.Null(result.Error);
        Assert.Equal("Sort on Row Id ASC ignored: not a field of the visual.", Assert.Single(result.Warnings));
        Assert.Equal("Total Sales", Assert.Single(result.Request.Sort).Measure);
        Assert.Equal(2, r.Sort.Count); // the caller's request is not modified
    }

    [Fact]
    public void SortCheck_IsSkippedWhenSortIsOff()
    {
        var r = Summary1();
        r.ApplySort = false;
        r.Sort.Add(new ExportSort { Table = "Orders", Column = "Row Id" });
        var result = Validate(r);
        Assert.Empty(result.Warnings);
        Assert.Same(r, result.Request);
    }

    [Fact]
    public void SavedSortField_IsSortable_EvenIfNotShown()
    {
        var report = Samples.Pbir(("pages/p/visuals/v/visual.json",
            Samples.VisualJson("v", new[] { Samples.ColumnExpr("Sales", "Region") }, sort: new[] { Samples.MeasureExpr("Sales", "Total") })));
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.PageName = "p";
        r.VisualName = "v";
        r.Sort.Add(new ExportSort { Table = "Sales", Measure = "Total", Direction = "Descending" });
        var result = VisualFieldValidator.Validate(r, report, FieldValidationMode.Strict);
        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
        Assert.Single(result.Request.Sort);
    }

    [Fact]
    public void Filters_AreNotRestricted()
    {
        var r = Summary1();
        r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"Orders","column":"Notes"},"filterType":1,"operator":"In","values":["x"]}"""));
        Assert.Null(Validate(r).Error);
    }

    private static string LevelExpr(string entity, string hierarchy, string level) =>
        $$$"""{"HierarchyLevel":{"Expression":{"Hierarchy":{"Expression":{"SourceRef":{"Entity":"{{{entity}}}"}},"Hierarchy":"{{{hierarchy}}}"}},"Level":"{{{level}}}"}}""";

    // A date column's auto date/time hierarchy: named after the column, as the embedding API reports it.
    private static string DateLevelExpr(string entity, string column, string level) =>
        $$$"""{"HierarchyLevel":{"Expression":{"Hierarchy":{"Expression":{"PropertyVariationSource":{"Expression":{"SourceRef":{"Entity":"{{{entity}}}"}},"Name":"Variation","Property":"{{{column}}}"}},"Hierarchy":"Date Hierarchy"}},"Level":"{{{level}}}"}}""";

    [Fact]
    public void HierarchyLevels_MatchByHierarchyAndLevel()
    {
        var report = Samples.Pbir(("pages/p/visuals/v/visual.json", Samples.VisualJson("v",
            new[]
            {
                LevelExpr("Sales", "Geo", "Area"), DateLevelExpr("Sales", "Order Date", "Year"),
                $$$"""{"Aggregation":{"Expression":{{{LevelExpr("Sales", "Geo", "Month")}}},"Function":5}}""",
                Samples.ColumnExpr("Sales", "Region")
            },
            sort: new[] { LevelExpr("Sales", "Geo", "Month") })));
        VisualExportRequest Request(params ExportField[] fields)
        {
            var r = Samples.Request(fields);
            r.PageName = "p";
            r.VisualName = "v";
            return r;
        }
        ExportField Level(string hierarchy, string level, string? aggregation = null) =>
            new() { Table = "Sales", Hierarchy = hierarchy, HierarchyLevel = level, Aggregation = aggregation };
        FieldValidationResult Check(VisualExportRequest r) => VisualFieldValidator.Validate(r, report, FieldValidationMode.Strict);

        var ok = Request(Level("geo", "AREA"), Level("Order Date", "Year"), Level("Geo", "Month", "CountNonNull"), Samples.Column("Sales", "Region"));
        ok.Sort.Add(new ExportSort { Table = "Sales", Hierarchy = "Geo", HierarchyLevel = "Month", Direction = "Descending" });
        ok.Sort.Add(new ExportSort { Table = "Sales", Hierarchy = "Geo", HierarchyLevel = "Area" });
        var result = Check(ok);
        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Request.Sort.Count);

        // A level isn't its column, a column isn't a level, and an aggregation of a level must match.
        var bad = Request(Level("Geo", "Month"), new ExportField { Table = "Sales", Column = "Geo" }, Level("Geo", "Month", "Sum"),
            Level("Order Date", "Month"));
        var refused = Check(bad);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        Assert.Contains("'Sales'[Geo].[Month], 'Sales'[Geo], Sum('Sales'[Geo].[Month]), 'Sales'[Order Date].[Month]", refused.Error);

        var sortOutside = Request(Samples.Column("Sales", "Region"));
        sortOutside.Sort.Add(new ExportSort { Table = "Sales", Hierarchy = "Order Date", HierarchyLevel = "Month" });
        Assert.Equal("Sort on Order Date.Month ASC ignored: not a field of the visual.", Assert.Single(Check(sortOutside).Warnings));
    }
}
