using PbiVisualExport.Web.Auth;
using PbiVisualExport.Web.Options;
using PbiVisualExport.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AzureAdOptions>(builder.Configuration.GetSection(AzureAdOptions.SectionName));
builder.Services.Configure<PowerBIOptions>(builder.Configuration.GetSection(PowerBIOptions.SectionName));
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
