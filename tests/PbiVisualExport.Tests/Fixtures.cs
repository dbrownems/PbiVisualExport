using System.Collections.Concurrent;
using System.Text.Json;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiVisualExport.Tests;

/// <summary>
/// Test fixtures from the repository: the TMDL models in <c>infra/models</c> and the PBIR
/// reports in <c>infra/reports</c> (the same definitions deployed to the test workspace).
/// </summary>
internal static class Fixtures
{
    public static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid DatasetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ReportId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PbiVisualExport.sln"))) return d.FullName;
        throw new InvalidOperationException("Repository root (PbiVisualExport.sln) not found above " + AppContext.BaseDirectory);
    }

    public static string TestsDirectory => Path.Combine(RepoRoot, "tests", "PbiVisualExport.Tests");

    private static readonly ConcurrentDictionary<string, TOM.Database> Databases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A model from <c>infra/models/{name}/definition</c> (shared: treat as read-only).</summary>
    public static TOM.Model Model(string name) => Database(name).Model;

    public static TOM.Database Database(string name) => Databases.GetOrAdd(name, n =>
        n == StoreSales ? BuildStoreSalesDatabase() : TOM.TmdlSerializer.DeserializeDatabaseFromFolder(ModelFolder(n)));

    public static string ModelFolder(string name) => Path.Combine(RepoRoot, "infra", "models", name, "definition");

    public const string StoreSales = "StoreSales";

    /// <summary>
    /// The part of the StoreSales sample model its report-measure visuals use (the model
    /// itself comes from a .pbix, so it isn't in <c>infra/models</c>).
    /// </summary>
    private static TOM.Database BuildStoreSalesDatabase()
    {
        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Units", DataType = TOM.DataType.Int64, SourceColumn = "Units" });
        sales.Measures.Add(new TOM.Measure { Name = "TotalSales", Expression = "SUM(Sales[Amount])", FormatString = "\\$#,0;(\\$#,0);\\$#,0" });
        sales.Measures.Add(new TOM.Measure { Name = "TotalUnits", Expression = "SUM(Sales[Units])", FormatString = "#,0" });

        var store = new TOM.Table { Name = "Store" };
        store.Columns.Add(new TOM.DataColumn { Name = "StoreId", DataType = TOM.DataType.Int64, SourceColumn = "StoreId", IsHidden = true });
        store.Columns.Add(new TOM.DataColumn { Name = "Chain", DataType = TOM.DataType.String, SourceColumn = "Chain" });
        store.Columns.Add(new TOM.DataColumn { Name = "Store Type", DataType = TOM.DataType.String, SourceColumn = "Store Type" });
        store.Measures.Add(new TOM.Measure { Name = "Total Stores", Expression = "DISTINCTCOUNT(Store[StoreId])", FormatString = "#,0" });

        var db = new TOM.Database { Name = StoreSales, CompatibilityLevel = 1600, Model = new TOM.Model() };
        db.Model.Tables.Add(sales);
        db.Model.Tables.Add(store);
        return db;
    }

    // ---- Reports ----

    public static string ReportFolder(string name) => Path.Combine(RepoRoot, "infra", "reports", name);

    /// <summary>The text parts of a report folder, keyed by definition path (as Fabric getDefinition returns them).</summary>
    public static Dictionary<string, string> ReportParts(string name)
    {
        var root = ReportFolder(name);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Path: Path.GetRelativePath(root, f).Replace('\\', '/'), File: f))
            .Where(p => !p.Path.StartsWith("StaticResources/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Path, p => File.ReadAllText(p.File).TrimStart('\uFEFF'), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A cached-definition snapshot, as <see cref="ReportDefinitionService"/> stores it.</summary>
    public static string Snapshot(IReadOnlyDictionary<string, string> parts, string? format = null) =>
        JsonSerializer.Serialize(new ReportDefinitionSnapshot
        {
            Format = format ?? ReportDefinition.DetectFormat(parts.Keys),
            Parts = new Dictionary<string, string>(parts, StringComparer.OrdinalIgnoreCase)
        }, ReportDefinition.JsonOptions);

    private static readonly ConcurrentDictionary<string, ReportDefinition> Reports = new(StringComparer.OrdinalIgnoreCase);

    public static ReportDefinition Report(string name) => Reports.GetOrAdd(name, n => ReportDefinition.Parse(Snapshot(ReportParts(n))));

    /// <summary>
    /// The request the browser client sends for a saved visual: its fields in order (columns,
    /// measures and aggregations) and its saved sort.
    /// </summary>
    public static VisualExportRequest RequestFromVisual(string reportName, string page, string visual)
    {
        var path = Path.Combine(ReportFolder(reportName), "definition", "pages", page, "visuals", visual, "visual.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var query = doc.RootElement.GetProperty("visual").GetProperty("query");
        var request = NewRequest(reportName, page, visual);
        foreach (var role in query.GetProperty("queryState").EnumerateObject())
        {
            if (!role.Value.TryGetProperty("projections", out var projections)) continue;
            foreach (var p in projections.EnumerateArray())
            {
                var r = VisualDefinition.ParseField(p.GetProperty("field")) ?? throw new InvalidOperationException("Unsupported field in " + path);
                var level = r.Level is not null;
                request.Fields.Add(new ExportField
                {
                    Table = r.Entity,
                    Measure = r.Kind == VisualFieldKind.Measure ? r.Property : null,
                    Column = r.Kind == VisualFieldKind.Measure || level ? null : r.Property,
                    Hierarchy = level ? r.Property : null,
                    HierarchyLevel = r.Level,
                    Aggregation = r.Aggregation,
                    Role = role.Name
                });
            }
        }
        if (query.TryGetProperty("sortDefinition", out var sd) && sd.TryGetProperty("sort", out var sort))
            foreach (var s in sort.EnumerateArray())
            {
                var r = VisualDefinition.ParseField(s.GetProperty("field"))!;
                var level = r.Level is not null;
                request.Sort.Add(new ExportSort
                {
                    Table = r.Entity,
                    Column = r.Kind == VisualFieldKind.Measure || level ? null : r.Property,
                    Measure = r.Kind == VisualFieldKind.Measure ? r.Property : null,
                    Hierarchy = level ? r.Property : null,
                    HierarchyLevel = r.Level,
                    Aggregation = r.Aggregation,
                    Direction = s.TryGetProperty("direction", out var d) ? d.GetString() : null
                });
            }
        return request;
    }

    public static VisualExportRequest NewRequest(string reportName = "Report", string? page = "Page1", string? visual = "visual1") => new()
    {
        WorkspaceId = WorkspaceId.ToString(),
        WorkspaceName = "Workspace",
        DatasetId = DatasetId.ToString(),
        DatasetName = "Dataset",
        ReportId = ReportId.ToString(),
        ReportName = reportName,
        PageName = page,
        VisualName = visual,
        VisualTitle = visual
    };

    /// <summary>A Power BI filter (IFilter) as the client sends it.</summary>
    public static JsonElement Filter(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
