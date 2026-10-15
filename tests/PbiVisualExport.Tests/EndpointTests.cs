using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiVisualExport.Tests;

/// <summary>
/// The endpoints over a TestServer with a stub catalog and pre-seeded metadata cache, so
/// nothing calls Power BI. Successful downloads (which query XMLA) are not exercised.
/// </summary>
public sealed class EndpointTests : IAsyncLifetime
{
    private const string Report = "LargeExportReport";
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "pve-tests-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_cacheDirectory);
        SeedCache(_cacheDirectory, "LargeExportTest", Report);

        _app = await StartHostAsync();
        _client = _app.GetTestClient();
    }

    /// <summary>Pre-seeds a metadata cache with a model and a report from the fixtures, under the stub catalog's IDs.</summary>
    private static void SeedCache(string directory, string model, string report)
    {
        var database = Fixtures.Database(model).Clone();
        database.Name = database.ID = model; // TMDL folders leave it empty; model.bim always has one
        File.WriteAllText(Path.Combine(directory, $"{Fixtures.DatasetId:N}.tmsl.json"), TOM.JsonSerializer.SerializeDatabase(database));
        File.WriteAllText(Path.Combine(directory, $"{Fixtures.ReportId:N}.report.json"),
            Fixtures.Snapshot(Fixtures.ReportParts(report)));
    }

    private async Task<WebApplication> StartHostAsync(
        Action<PbiVisualExportOptions>? options = null, Action<PbiVisualExportBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(EndpointTests).Assembly.GetName().Name
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IPowerBiCatalog>(new StubCatalog(StubCatalog.Workspace(Fixtures.WorkspaceId, "Workspace",
            new[] { (Fixtures.DatasetId, "Dataset") }, new CatalogReport(Fixtures.ReportId, Report, Fixtures.DatasetId))));
        builder.Services.AddAuthentication(TestAuth.Name).AddScheme<AuthenticationSchemeOptions, TestAuth>(TestAuth.Name, null);
        var export = builder.Services.AddPbiVisualExport(o =>
        {
            o.AllowedWorkspaceIds.Add(Fixtures.WorkspaceId);
            o.MetadataCache.DiskCacheDirectory = _cacheDirectory;
            o.MetadataCache.TtlMinutes = 0;
            o.MetadataCache.RefreshOnMissSeconds = 0;
            options?.Invoke(o);
        }).UseCredential(new FakeCredential());
        configure?.Invoke(export);

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPbiVisualExport("/pbi-export");
        await app.StartAsync();
        return app;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_cacheDirectory, recursive: true); } catch (IOException) { }
    }

    private static VisualExportRequest Summary1() => Fixtures.RequestFromVisual(Report, "Page2", "summary1");

    private static HttpRequestMessage Post(string path, object? body, string? user = "u1", string contentType = "application/json")
    {
        var json = body as string ?? JsonSerializer.Serialize(body, ExportHandler.Json);
        var m = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, contentType) };
        if (user is not null) m.Headers.Add(TestAuth.Header, user);
        return m;
    }

    private static HttpRequestMessage Get(string path, string? user = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, path);
        if (user is not null) m.Headers.Add(TestAuth.Header, user);
        return m;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private static async Task AssertOk(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [InlineData("client.js")]
    [InlineData("client.mjs")]
    public async Task ClientScript_IsServedWithValidators(string file)
    {
        var response = await _client.GetAsync("/pbi-export/" + file);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl!.ToString());
        Assert.Equal("2", response.Headers.GetValues(ExportHandler.ContractHeader).Single());
        var etag = response.Headers.ETag!;
        Assert.NotEmpty(await response.Content.ReadAsStringAsync());

        var again = new HttpRequestMessage(HttpMethod.Get, "/pbi-export/" + file);
        again.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await _client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task Prepare_RequiresAnAuthenticatedUser()
    {
        var response = await _client.SendAsync(Post("/pbi-export/prepare", Summary1(), user: null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("prepare")]
    [InlineData("bench")]
    public async Task IdentityProviderFailure_IsJson500(string endpoint)
    {
        await using var app = await StartHostAsync(
            o => o.Export.EnableBenchmark = true,
            b => b.AddEffectiveIdentity<ThrowingIdentityProvider>());
        using var client = app.GetTestClient();

        var response = await client.SendAsync(Post("/pbi-export/" + endpoint, Summary1()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("identity boom", (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Prepare_RequiresJson()
    {
        var response = await _client.SendAsync(Post("/pbi-export/prepare", "{}", contentType: "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("2", response.Headers.GetValues(ExportHandler.ContractHeader).Single());
    }

    [Fact]
    public async Task Prepare_RejectsMalformedJson()
    {
        var response = await _client.SendAsync(Post("/pbi-export/prepare", "{ nope"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not valid JSON", (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Prepare_ListsRequestProblems()
    {
        var response = await _client.SendAsync(Post("/pbi-export/prepare", new { fields = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problems = (await Json(response)).GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("workspaceId is required.", problems);
        Assert.Contains("reportId is required.", problems);
        Assert.Contains("fields must list at least one field of the visual.", problems);
    }

    [Fact]
    public async Task Prepare_RefusesWorkspacesNotAllowed()
    {
        var r = Summary1();
        r.WorkspaceId = Guid.NewGuid().ToString();
        var response = await _client.SendAsync(Post("/pbi-export/prepare", r));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("are not allowed", (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Prepare_ChecksAndPlansTheVisual()
    {
        var response = await _client.SendAsync(Post("/pbi-export/prepare", Summary1()));
        await AssertOk(response);
        var body = await Json(response);
        Assert.Equal(7, body.GetProperty("columns").GetInt32());
        Assert.Matches(@"^summary1-\d{8}-\d{6}\.csv$", body.GetProperty("fileName").GetString());
        Assert.Equal("Dataset", body.GetProperty("datasetName").GetString());
        Assert.Equal(0, body.GetProperty("warnings").GetArrayLength());
        Assert.False(body.TryGetProperty("ticket", out _));
    }

    [Fact]
    public async Task Prepare_RefusesFieldsTheVisualDoesNotShow()
    {
        var r = Summary1();
        r.Fields.Add(Samples.Column("Orders", "Customer Name"));
        var response = await _client.SendAsync(Post("/pbi-export/prepare", r));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("'Orders'[Customer Name]", (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Prepare_AllowsAnyFieldOfTheVisualsFieldParameter()
    {
        // FieldParamReport fpTable was saved with Category selected in the Fields slicer; the model lists Segment too.
        var cache = Path.Combine(Path.GetTempPath(), "pve-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            SeedCache(cache, "FieldParamTest", "FieldParamReport");
            await using var app = await StartHostAsync(o => o.MetadataCache.DiskCacheDirectory = cache);
            using var client = app.GetTestClient();
            var r = Fixtures.RequestFromVisual("FieldParamReport", "Page1", "fpTable");
            r.ReportName = null; // the stub catalog's report has another name
            r.Fields[r.Fields.FindIndex(f => f.Column == "Category")] = Samples.Column("Product", "Segment");

            var response = await client.SendAsync(Post("/pbi-export/prepare", r));
            await AssertOk(response);
            Assert.Equal(3, (await Json(response)).GetProperty("columns").GetInt32());

            r.Fields.Add(Samples.Column("Product", "ProductKey"));
            response = await client.SendAsync(Post("/pbi-export/prepare", r));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Csv_ValidatesBeforeStreaming()
    {
        var r = Summary1();
        r.Fields.Add(Samples.Column("Orders", "Customer Name"));
        var response = await _client.SendAsync(Post("/pbi-export/csv", r));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OptionalEndpoints_AreOffByDefault()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(Post("/pbi-export/bench", Summary1()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(Get("/pbi-export/xmla-auth", user: "u1"))).StatusCode);
    }

    /// <summary>Signs in the user named by the <c>X-Test-User</c> header (as an oid claim).</summary>
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "Test";
        public const string Header = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var user)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(new[] { new Claim("oid", user.ToString()), new Claim(ClaimTypes.Name, user.ToString()) }, Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
        }
    }
}
