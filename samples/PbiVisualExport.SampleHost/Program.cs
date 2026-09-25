using PbiVisualExport;
using PbiVisualExport.SampleHost;
using PbiVisualExport.SampleHost.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration cascades (later wins): appsettings.json (committed defaults, no environment
// IDs) < appsettings.{Environment}.json < user secrets (Development only - the local secret
// store, see <UserSecretsId>) < environment variables (deployed, e.g. PowerBI__WorkspaceId,
// PbiVisualExport__Credential__Mode) < command line. CreateBuilder wires all of these.
builder.Services.AddOptions<PowerBIOptions>()
    .Bind(builder.Configuration.GetSection(PowerBIOptions.SectionName))
    .Validate(o => Guid.TryParse(o.WorkspaceId, out _),
        $"PowerBI:WorkspaceId is not configured (environment: {builder.Environment.EnvironmentName}). " +
        "Locally: dotnet user-secrets set PowerBI:WorkspaceId <id> --project samples/PbiVisualExport.SampleHost " +
        "(user secrets load only in Development; dotnet run sets it via launchSettings.json). " +
        "Deployed: set the PowerBI__WorkspaceId environment variable.")
    .ValidateOnStart();

// The export module. The sample exports only from the workspace it embeds, so that workspace
// is the allow-list; a real host would typically register an IVisualExportAuthorizer too.
builder.Services.AddPbiVisualExport(
    builder.Configuration.GetSection(PbiVisualExportOptions.DefaultSectionName),
    o =>
    {
        if (Guid.TryParse(builder.Configuration["PowerBI:WorkspaceId"], out var ws) && !o.AllowedWorkspaceIds.Contains(ws))
            o.AllowedWorkspaceIds.Add(ws);
    });

builder.Services.AddScoped<PbiEmbedService>();
builder.Services.AddControllersWithViews();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
// Required after an explicit UseRouting(): the export endpoints carry authorization metadata
// (even when AllowAnonymous), and the middleware WebApplication adds implicitly would run
// before routing and never see it. (A host with sign-in has UseAuthentication() before this.)
app.UseAuthorization();

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapControllers();

// The sample has no user sign-in, so it opts the export endpoints out of the module's default
// RequireAuthorization(). A real host keeps it (and its own authentication) in place.
app.MapPbiVisualExport("/pbi-export").AllowAnonymous();

app.Run();
