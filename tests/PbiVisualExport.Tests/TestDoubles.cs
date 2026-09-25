using Azure.Core;
using Microsoft.Extensions.Options;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiVisualExport.Tests;

internal static class TestOptions
{
    public static IOptions<PbiVisualExportOptions> Create(Action<PbiVisualExportOptions>? configure = null)
    {
        var o = new PbiVisualExportOptions();
        configure?.Invoke(o);
        return Options.Create(o);
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class FakeCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

/// <summary>A fixed workspace catalog; counts lookups.</summary>
internal sealed class StubCatalog : IPowerBiCatalog
{
    private readonly Dictionary<Guid, WorkspaceCatalog> _workspaces;

    public StubCatalog(params WorkspaceCatalog[] workspaces) => _workspaces = workspaces.ToDictionary(w => w.Id);

    public int Calls { get; private set; }

    public static WorkspaceCatalog Workspace(Guid id, string name, (Guid Id, string Name)[] datasets, params CatalogReport[] reports) =>
        new(id, name, datasets.ToDictionary(d => d.Id, d => d.Name), reports.ToDictionary(r => r.Id));

    public Task<WorkspaceCatalog> GetAsync(Guid workspaceId, bool forceRefresh = false, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(_workspaces.TryGetValue(workspaceId, out var w)
            ? w
            : new WorkspaceCatalog(workspaceId, "", new Dictionary<Guid, string>(), new Dictionary<Guid, CatalogReport>()));
    }

    public Task<WorkspaceCatalog> GetContainingAsync(Guid workspaceId, Guid? datasetId, Guid? reportId, CancellationToken ct = default) =>
        GetAsync(workspaceId, false, ct);
}

internal sealed class DelegateAuthorizer(Func<VisualExportContext, VisualExportAuthorization> decide) : IVisualExportAuthorizer
{
    public VisualExportContext? LastContext { get; private set; }

    public ValueTask<VisualExportAuthorization> AuthorizeAsync(VisualExportContext context, CancellationToken cancellationToken)
    {
        LastContext = context;
        return ValueTask.FromResult(decide(context));
    }
}

/// <summary>Small in-code models and report definitions for targeted tests.</summary>
internal static class Samples
{
    /// <summary>
    /// Sales(Region, Amount "#,0.00", Order Date, Is Online, Month Name sorted by Month Number, Month Number hidden;
    /// measures Total, Count) and a table and column with characters that need escaping.
    /// </summary>
    public static TOM.Model Model()
    {
        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", FormatString = "#,0.00" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Order Date", DataType = TOM.DataType.DateTime, SourceColumn = "Order Date", FormatString = "yyyy-mm-dd" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Is Online", DataType = TOM.DataType.Boolean, SourceColumn = "Is Online" });
        var monthNumber = new TOM.DataColumn { Name = "Month Number", DataType = TOM.DataType.Int64, SourceColumn = "Month Number", IsHidden = true };
        var monthName = new TOM.DataColumn { Name = "Month Name", DataType = TOM.DataType.String, SourceColumn = "Month Name" };
        sales.Columns.Add(monthNumber);
        sales.Columns.Add(monthName);
        monthName.SortByColumn = monthNumber;
        sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", FormatString = "#,0" });
        sales.Measures.Add(new TOM.Measure { Name = "Count", Expression = "COUNTROWS(Sales)" });

        var odd = new TOM.Table { Name = "O'Brien" };
        odd.Columns.Add(new TOM.DataColumn { Name = "a]b", DataType = TOM.DataType.String, SourceColumn = "ab" });
        odd.Measures.Add(new TOM.Measure { Name = "m]x", Expression = "1" });

        var db = new TOM.Database { Name = "Samples", CompatibilityLevel = 1600, Model = new TOM.Model() };
        db.Model.Tables.Add(sales);
        db.Model.Tables.Add(odd);
        return db.Model;
    }

    public static ExportField Column(string table, string column) => new() { Table = table, Column = column };
    public static ExportField Measure(string table, string measure) => new() { Table = table, Measure = measure };
    public static ExportField Agg(string table, string column, string aggregation) => new() { Table = table, Column = column, Aggregation = aggregation };

    public static VisualExportRequest Request(params ExportField[] fields)
    {
        var r = Fixtures.NewRequest();
        r.Fields.AddRange(fields);
        return r;
    }

    /// <summary>A PBIR report from parts (paths under <c>definition/</c> may omit the prefix).</summary>
    public static ReportDefinition Pbir(params (string Path, string Json)[] parts)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["definition/report.json"] = "{}" };
        foreach (var (path, json) in parts)
            dict[path.StartsWith("definition/", StringComparison.OrdinalIgnoreCase) ? path : "definition/" + path] = json;
        return ReportDefinition.Parse(Fixtures.Snapshot(dict));
    }

    public static ReportDefinition Legacy() =>
        ReportDefinition.Parse(Fixtures.Snapshot(new Dictionary<string, string> { ["report.json"] = "{}" }));

    public static string ColumnExpr(string entity, string property) =>
        $$$"""{"Column":{"Expression":{"SourceRef":{"Entity":"{{{entity}}}"}},"Property":"{{{property}}}"}}""";

    public static string MeasureExpr(string entity, string property) =>
        $$$"""{"Measure":{"Expression":{"SourceRef":{"Entity":"{{{entity}}}"}},"Property":"{{{property}}}"}}""";

    public static string AggExpr(string entity, string property, int function) =>
        $$$"""{"Aggregation":{"Expression":{{{ColumnExpr(entity, property)}}},"Function":{{{function}}}}}""";

    /// <summary>A <c>visual.json</c> with the given projections (one role) and saved sort.</summary>
    public static string VisualJson(string name, string[] projections, string[]? sort = null)
    {
        var proj = string.Join(",", projections.Select(p => $$"""{"field":{{p}}}"""));
        var sortJson = sort is null ? "" :
            $$""","sortDefinition":{"sort":[{{string.Join(",", sort.Select(s => $$"""{"field":{{s}},"direction":"Descending"}"""))}}]}""";
        return $$"""{"name":"{{name}}","visual":{"visualType":"tableEx","query":{"queryState":{"Values":{"projections":[{{proj}}]""" + "}}" + sortJson + "}}}";
    }
}
