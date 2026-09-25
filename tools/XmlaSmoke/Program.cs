using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using TOM = Microsoft.AnalysisServices.Tabular;

// Settings cascade (later wins): appsettings.json < user secrets < env vars < command line.
// User secrets are always loaded (dev-only tool) and shared with the web app's secret store.
string projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

string Setting(string key, bool required = false)
{
    string v = config[key];
    if (required && string.IsNullOrWhiteSpace(v))
    {
        Console.Error.WriteLine($"{key} is not configured. Set it with one of:\n" +
            $"  dotnet user-secrets set {key} <value> --project tools/XmlaSmoke\n" +
            $"  $env:{key.Replace(":", "__")} = '<value>'\n" +
            $"  dotnet run --project tools/XmlaSmoke -- --{key} <value>");
        Environment.Exit(2);
    }
    return string.IsNullOrWhiteSpace(v) ? null : v;
}

const string scope = "https://analysis.windows.net/powerbi/api/.default";
// Microsoft's well-known public client ID for the Azure CLI (not environment-specific).
const string azureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
string tenant = Setting("AzureAd:TenantId");
string datasetId = Setting("XmlaSmoke:DatasetId", required: true);
string bimPath = Path.Combine(projectDir, Setting("XmlaSmoke:ModelBimPath") ?? "model.bim");
Console.WriteLine($"[config] dataset={datasetId} tenant={tenant ?? "(organizations)"} bim={bimPath}");

// ---- 1. Acquire PBI token (for executeQueries data path) ----
AccessToken tok;
var ctx = new TokenRequestContext(new[] { scope });
try
{
    tok = await new AzureCliCredential().GetTokenAsync(ctx);
    Console.WriteLine($"[token] AzureCli OK, len={tok.Token.Length}");
}
catch (Exception ex)
{
    Console.WriteLine($"[token] AzureCli failed: {ex.Message}. Device code...");
    var dc = new DeviceCodeCredential(new DeviceCodeCredentialOptions
    {
        TenantId = tenant,
        ClientId = azureCliClientId,
        DeviceCodeCallback = (i, _) => { Console.WriteLine(i.Message); return Task.CompletedTask; }
    });
    tok = await dc.GetTokenAsync(ctx);
}

// ---- 2. TOM metadata FROM DOWNLOADED TMSL (no XMLA) ----
Console.WriteLine("\n=== TOM metadata (from getDefinition TMSL) ===");
string tmsl = await File.ReadAllTextAsync(Path.GetFullPath(bimPath));
TOM.Database db = TOM.JsonSerializer.DeserializeDatabase(tmsl);
TOM.Model model = db.Model;
Console.WriteLine($"Loaded model. Tables={model.Tables.Count}");

string sampleTable = null, sampleColumn = null;
foreach (TOM.Table t in model.Tables)
{
    var cols = t.Columns.Cast<TOM.Column>().Where(c => c.Type != TOM.ColumnType.RowNumber).ToList();
    Console.WriteLine($"\nTable: {t.Name} (hidden={t.IsHidden}) cols={cols.Count} measures={t.Measures.Count}");
    foreach (TOM.Column c in cols.Take(4))
        Console.WriteLine($"   col {c.Name,-22} type={c.DataType,-8} hidden={c.IsHidden,-5} fmt='{c.FormatString}'");
    foreach (TOM.Measure m in t.Measures.Cast<TOM.Measure>().Take(2))
        Console.WriteLine($"   msr {m.Name,-22} fmt='{m.FormatString}'");
    if (sampleTable == null && !t.IsHidden && cols.Any(c => !c.IsHidden))
    {
        sampleTable = t.Name;
        sampleColumn = cols.First(c => !c.IsHidden).Name;
    }
}

Console.WriteLine("\n=== M expressions (parameters / shared) ===");
foreach (TOM.NamedExpression e in model.Expressions)
    Console.WriteLine($"   expr {e.Name} kind={e.Kind}");
if (model.Expressions.Count == 0) Console.WriteLine("   (none)");

// ---- 3. DATA via REST executeQueries (paginates to any size) ----
Console.WriteLine($"\n=== executeQueries: TOPN(3) '{sampleTable}' ===");
using var http = new HttpClient();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok.Token);
string dax = $"EVALUATE TOPN(3, '{sampleTable}')";
var payload = new
{
    queries = new[] { new { query = dax } },
    serializerSettings = new { includeNulls = true }
};
var reqBody = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
var url = $"https://api.powerbi.com/v1.0/myorg/datasets/{datasetId}/executeQueries";
var resp = await http.PostAsync(url, reqBody);
var json = await resp.Content.ReadAsStringAsync();
Console.WriteLine($"HTTP {(int)resp.StatusCode}");
if (!resp.IsSuccessStatusCode) { Console.WriteLine(json[..Math.Min(500, json.Length)]); return; }
using var docu = JsonDocument.Parse(json);
var rows = docu.RootElement.GetProperty("results")[0].GetProperty("tables")[0].GetProperty("rows");
Console.WriteLine($"rows returned: {rows.GetArrayLength()}");
foreach (var row in rows.EnumerateArray().Take(3))
    Console.WriteLine("   " + string.Join(" | ", row.EnumerateObject().Take(6).Select(p => $"{p.Name}={p.Value}")));

Console.WriteLine("\nSMOKE OK: TOM metadata (downloaded TMSL) + executeQueries data both work.");
