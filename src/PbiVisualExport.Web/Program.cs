using PbiVisualExport.Web.Auth;
using PbiVisualExport.Web.Options;
using PbiVisualExport.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration cascades (later wins): appsettings.json (committed defaults, no environment
// IDs) < appsettings.{Environment}.json < user secrets (Development only - the local secret
// store, see <UserSecretsId>) < environment variables (deployed, e.g. PowerBI__WorkspaceId)
// < command line. CreateBuilder wires all of these; don't add providers here.
builder.Services.Configure<AzureAdOptions>(builder.Configuration.GetSection(AzureAdOptions.SectionName));
builder.Services.AddOptions<PowerBIOptions>()
    .Bind(builder.Configuration.GetSection(PowerBIOptions.SectionName))
    .Validate(o => Guid.TryParse(o.WorkspaceId, out _),
        $"PowerBI:WorkspaceId is not configured (environment: {builder.Environment.EnvironmentName}). " +
        "Locally: dotnet user-secrets set PowerBI:WorkspaceId <id> --project src/PbiVisualExport.Web " +
        "(user secrets load only in Development; dotnet run sets it via launchSettings.json). " +
        "Deployed: set the PowerBI__WorkspaceId environment variable.")
    .ValidateOnStart();
builder.Services.Configure<ExportOptions>(builder.Configuration.GetSection(ExportOptions.SectionName));
builder.Services.Configure<MetadataCacheOptions>(builder.Configuration.GetSection(MetadataCacheOptions.SectionName));

builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<PbiTokenProvider>();
builder.Services.AddSingleton<XmlaTokenProvider>();
builder.Services.AddSingleton<XmlaConnectionFactory>();
builder.Services.AddSingleton<PowerBiCatalog>();
builder.Services.AddSingleton<MetadataCache>();
builder.Services.AddSingleton<FabricDefinitionClient>();
builder.Services.AddSingleton<SemanticModelMetadataService>();
builder.Services.AddSingleton<ReportDefinitionService>();
builder.Services.AddSingleton<DaxQueryBuilder>();
builder.Services.AddScoped<PbiEmbedService>();
builder.Services.AddScoped<CsvExportService>();

builder.Services.AddControllersWithViews();
// Protects the form-POST download (/export/csv/form). No X-Frame-Options: the page is
// an embedding sample and may itself be framed by a host app.
builder.Services.AddAntiforgery(o => o.SuppressXFrameOptionsHeader = true);

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapControllers();

app.Run();
