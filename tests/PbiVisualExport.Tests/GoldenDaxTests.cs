using System.Text;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiVisualExport.Tests;

/// <summary>
/// Golden-file tests: the DAX generated for realistic requests (the fixture reports' visuals
/// plus filters and slicer states as the browser client sends them) must match
/// <c>Golden/{case}.dax</c> exactly. The golden queries were checked against the deployed
/// test models. After an intended change to the generated DAX, regenerate them with
/// <c>UPDATE_GOLDEN=1 dotnet test</c> and review the diff.
/// </summary>
public class GoldenDaxTests
{
    private const string Large = "LargeExportTest";
    private const string LargeReport = "LargeExportReport";

    private sealed record GoldenCase(string Name, string Model, string? Report, Func<VisualExportRequest> Request, long MaxRows = 0);

    private static readonly GoldenCase[] AllCases =
    {
        // 31 columns of every data type, with model format strings; no saved sort (implicit order).
        new("large-detail1", Large, null, () => Fixtures.RequestFromVisual(LargeReport, "Page1", "detail1")),

        // Raw values and engine order: no FORMAT(), no ORDER BY.
        new("large-detail1-raw-unsorted", Large, null, () =>
        {
            var r = Fixtures.RequestFromVisual(LargeReport, "Page1", "detail1");
            r.ApplyFormatStrings = false;
            r.ApplySort = false;
            return r;
        }),

        // Implicit aggregations (Sum, Avg) and model measures, with the visual's saved sort; row cap.
        new("large-summary1-maxrows", Large, null, () => Fixtures.RequestFromVisual(LargeReport, "Page2", "summary1"), MaxRows: 1000),

        // A measure hidden on the visual is computed (it decides which rows are blank) but not output.
        new("large-summary1-hidden-measure", Large, null, () =>
        {
            var r = Fixtures.RequestFromVisual(LargeReport, "Page2", "summary1");
            r.Fields.Single(f => f.Measure == "Return Rate").Hidden = true;
            return r;
        }),

        // Slicers and column filters: basic In / NotIn, advanced And / Or conditions, dates.
        new("large-table3-column-filters", Large, null, () =>
        {
            var r = Fixtures.RequestFromVisual(LargeReport, "Page3", "table3");
            r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"Orders","column":"Region"},"filterType":1,"operator":"In","values":["East","West"]}"""));
            r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"Orders","column":"Category"},"filterType":1,"operator":"NotIn","values":["Toys"]}"""));
            r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Orders","column":"Order Year"},"filterType":0,"logicalOperator":"And","conditions":[{"operator":"GreaterThanOrEqual","value":2022},{"operator":"LessThan","value":2025}]}"""));
            r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Orders","column":"Order Date"},"filterType":0,"logicalOperator":"Or","conditions":[{"operator":"LessThan","value":"2022-03-01T00:00:00.000Z"},{"operator":"IsBlank"}]}"""));
            r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Orders","column":"Product Name"},"filterType":0,"logicalOperator":"And","conditions":[{"operator":"Contains","value":"Widget \"Pro\""}]}"""));
            return r;
        }),

        // Visual-level measure filters: on a measure the visual shows, on a filter-only measure,
        // on an implicit aggregation; plus a Top N filter.
        new("large-summary1-measure-filters", Large, null, () =>
        {
            var r = Fixtures.RequestFromVisual(LargeReport, "Page2", "summary1");
            r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Orders","measure":"Total Sales"},"filterType":0,"logicalOperator":"And","conditions":[{"operator":"GreaterThan","value":1000}]}"""));
            r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Orders","measure":"Order Count"},"filterType":0,"logicalOperator":"Or","conditions":[{"operator":"LessThan","value":10},{"operator":"GreaterThanOrEqual","value":"500"}]}"""));
            r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Orders","column":"Quantity","aggregationFunction":"Sum"},"filterType":0,"logicalOperator":"And","conditions":[{"operator":"IsNotBlank"}]}"""));
            r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#topN","target":{"table":"Orders","column":"Region"},"filterType":5,"operator":"Top","itemCount":3,"orderBy":{"table":"Orders","measure":"Total Sales"}}"""));
            return r;
        }),

        // A field parameter's display column: grouped with its hidden key column and ordered by its Sort By column.
        new("fieldparam-table1", "FieldParamTest", null, () =>
        {
            var r = Fixtures.RequestFromVisual("FieldParamReport", "Page1", "table1");
            r.Sort.Clear();
            r.Sort.Add(new ExportSort { Table = "Fields", Column = "Fields", Direction = "Ascending" });
            return r;
        }),

        // A field parameter expanded by the client to the column it currently selects.
        new("fieldparam-fptable", "FieldParamTest", null, () => Fixtures.RequestFromVisual("FieldParamReport", "Page1", "fpTable")),

        // DirectQuery model whose Sales source query is filtered by dynamic M parameters bound to the
        // picker columns: slicers on them set RegionParam (single-select, scalar) and CategoryParam
        // (multi-select, a list) with MPARAMETER, and stay as column filters, as in Power BI's own query.
        new("dq-table1-mparameter-slicers", "DqParamTest", null, () =>
        {
            var r = Fixtures.RequestFromVisual("DqParamReport", "Page1", "table1");
            r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"RegionPicker","column":"RegionChoice"},"filterType":1,"operator":"In","values":["West"],"requireSingleSelection":true}"""));
            r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"CategoryPicker","column":"CategoryChoice"},"filterType":1,"operator":"In","values":["Bikes","Clothing"],"requireSingleSelection":false}"""));
            return r;
        }),

        // Report-level measures (one built on another), defined in the query; sorted by one of them.
        new("storesales-rmtable-report-measures", Fixtures.StoreSales, Fixtures.StoreSales,
            () => Fixtures.RequestFromVisual(Fixtures.StoreSales, "Page3", "rmTable")),

        // Hierarchy levels as fields (grouped on the level's column, headed by the level name), an
        // implicit aggregation of a level, and the visual's saved sort on a level.
        new("large-table4b-hierarchy-levels", Large, null, () => Fixtures.RequestFromVisual(LargeReport, "Page4", "table4b")),

        // Every implicit aggregation Power BI offers (verified against native export: Count (Distinct)
        // counts BLANK, Count is COUNTA, Standard deviation and Variance are the population forms).
        new("large-table5-aggregations", Large, null, () => Fixtures.RequestFromVisual(LargeReport, "Page5", "table5")),
    };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in AllCases) data.Add(c.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void GeneratedDaxMatchesGolden(string name)
    {
        var c = AllCases.Single(x => x.Name == name);
        var model = Fixtures.Model(c.Model);
        var report = c.Report is null ? null : Fixtures.Report(c.Report);
        var request = c.Request();
        var missing = DaxQueryBuilder.FindUnresolvedFields(request, model)
            .Where(u => !u.SortOnly && (u.MeasureName is null || report?.FindMeasure(u.MeasureName) is null))
            .Select(u => u.Label)
            .ToList();
        Assert.True(missing.Count == 0, "Fields not in the model or report: " + string.Join(", ", missing));

        var plan = new DaxQueryBuilder().Build(request, model, report);
        var actual = Render(plan, c.MaxRows);

        var path = Path.Combine(Fixtures.TestsDirectory, "Golden", c.Name + ".dax");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }
        Assert.True(File.Exists(path), $"Golden file {path} is missing. Run with UPDATE_GOLDEN=1 to create it. Generated:\n{actual}");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    /// <summary>The query plus the plan's metadata, so column headers, sort and warnings are pinned too.</summary>
    private static string Render(DaxPlan plan, long maxRows)
    {
        var sb = new StringBuilder();
        sb.Append("// columns: ").Append(string.Join(" | ", plan.Columns.Select(c => $"{c.Alias}={c.Header}{(c.IsMeasure ? " (measure)" : "")}"))).Append('\n');
        sb.Append("// sort: ").Append(plan.SortDescription).Append('\n');
        if (plan.ReportMeasures.Count > 0) sb.Append("// report measures: ").Append(string.Join(", ", plan.ReportMeasures)).Append('\n');
        foreach (var f in plan.MeasureFilters) sb.Append("// measure filter: ").Append(f).Append('\n');
        foreach (var p in plan.Parameters) sb.Append("// M parameter: ").Append(p).Append('\n');
        foreach (var w in plan.Warnings) sb.Append("// warning: ").Append(w).Append('\n');
        if (maxRows > 0) sb.Append("// maxRows: ").Append(maxRows).Append('\n');
        sb.Append(plan.BuildQuery(maxRows)).Append('\n');
        return sb.ToString();
    }
}
