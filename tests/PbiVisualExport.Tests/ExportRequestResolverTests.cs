using Microsoft.AspNetCore.Http;

namespace PbiVisualExport.Tests;

public class ExportRequestResolverTests
{
    private static readonly Guid Ws = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherWs = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Ds = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid OtherDs = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Rpt = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid OrphanRpt = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    private readonly StubCatalog _catalog = new(StubCatalog.Workspace(Ws, "Sales WS", new[] { (Ds, "Sales Model") },
        new CatalogReport(Rpt, "Sales Report", Ds),
        new CatalogReport(OrphanRpt, "Orphan", OtherDs)));

    private DelegateAuthorizer _authorizer = new(_ => VisualExportAuthorization.Allow());

    private ExportRequestResolver Resolver(Action<PbiVisualExportOptions>? configure = null) =>
        new(_catalog, _authorizer, TestOptions.Create(o =>
        {
            o.AllowedWorkspaceIds.Add(Ws);
            o.AllowedWorkspaceIds.Add(OtherWs);
            configure?.Invoke(o);
        }));

    private static VisualExportRequest Request(string? ws = null, string? rpt = null, string? ds = null)
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.WorkspaceId = ws ?? Ws.ToString().ToUpperInvariant();
        r.ReportId = rpt ?? Rpt.ToString();
        r.DatasetId = ds;
        r.WorkspaceName = r.ReportName = r.DatasetName = null;
        return r;
    }

    private Task<ResolveResult> Resolve(VisualExportRequest r, ExportRequestResolver? resolver = null) =>
        (resolver ?? Resolver()).ResolveAsync(new DefaultHttpContext(), r, CancellationToken.None);

    [Fact]
    public async Task Valid_FillsIdsAndNamesFromTheCatalog()
    {
        var r = Request();
        var result = await Resolve(r);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        var c = result.Context!;
        Assert.Equal((Ws, "Sales WS", Ds, "Sales Model", Rpt, "Sales Report"), (c.WorkspaceId, c.WorkspaceName, c.DatasetId, c.DatasetName, c.ReportId, c.ReportName));
        Assert.Same(r, c.Request);
        Assert.Equal(Ws.ToString(), r.WorkspaceId);
        Assert.Equal(Ds.ToString(), r.DatasetId);
        Assert.Equal(("Sales WS", "Sales Model", "Sales Report"), (r.WorkspaceName, r.DatasetName, r.ReportName));
        Assert.Same(c, _authorizer.LastContext);
    }

    [Fact]
    public async Task MatchingNames_AreAccepted_CaseInsensitiveAndTrimmed()
    {
        var r = Request(ds: Ds.ToString());
        r.WorkspaceName = " sales ws ";
        r.ReportName = "SALES REPORT";
        r.DatasetName = "Sales Model";
        Assert.Equal(StatusCodes.Status200OK, (await Resolve(r)).StatusCode);
    }

    [Fact]
    public async Task MissingAndMalformedIds_AreBadRequest_BeforeAnyLookup()
    {
        var r = Request(ws: "", rpt: "not-a-guid", ds: "also-bad");
        r.Fields.Clear();
        var result = await Resolve(r);

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal(new[]
        {
            "workspaceId is required.", "reportId must be a GUID.", "datasetId must be a GUID.",
            "fields must list at least one field of the visual."
        }, result.Problems);
        Assert.StartsWith("Invalid export request: workspaceId is required. reportId", result.Error);
        Assert.Equal(0, _catalog.Calls);
    }

    [Fact]
    public async Task WorkspaceNotAllowed_IsForbidden_BeforeAnyLookup()
    {
        var other = Guid.NewGuid();
        var result = await Resolve(Request(ws: other.ToString()));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal($"Exports from workspace {other} are not allowed.", result.Error);
        Assert.Equal(0, _catalog.Calls);
    }

    [Fact]
    public async Task EmptyAllowList_LeavesTheDecisionToTheAuthorizer()
    {
        var result = await Resolve(Request(ws: Ws.ToString()), Resolver(o => o.AllowedWorkspaceIds.Clear()));
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    [Fact]
    public async Task UnknownReport_IsBadRequest()
    {
        var missing = Guid.NewGuid();
        var result = await Resolve(Request(rpt: missing.ToString()));
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal($"reportId {missing} was not found in workspace {Ws}.", Assert.Single(result.Problems));
    }

    [Fact]
    public async Task ReportInAnotherWorkspace_IsNotFound()
    {
        var result = await Resolve(Request(ws: OtherWs.ToString()));
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("was not found in workspace", result.Error);
    }

    [Fact]
    public async Task DatasetMismatch_IsBadRequest()
    {
        var result = await Resolve(Request(ds: OtherDs.ToString()));
        Assert.Equal($"report {Rpt} is bound to dataset {Ds}, not {OtherDs}.", Assert.Single(result.Problems));
    }

    [Fact]
    public async Task ReportOnADatasetOutsideTheWorkspace_IsBadRequest()
    {
        var result = await Resolve(Request(rpt: OrphanRpt.ToString()));
        Assert.Contains("which is not in workspace", Assert.Single(result.Problems));
    }

    [Fact]
    public async Task NameMismatches_AreAllReported()
    {
        var r = Request();
        r.WorkspaceName = "Wrong WS";
        r.ReportName = "Wrong Report";
        r.DatasetName = "Wrong Model";
        var result = await Resolve(r);
        Assert.Equal(new[]
        {
            $"workspaceName 'Wrong WS' does not match workspace {Ws} ('Sales WS').",
            $"reportName 'Wrong Report' does not match report {Rpt} ('Sales Report').",
            $"datasetName 'Wrong Model' does not match dataset {Ds} ('Sales Model')."
        }, result.Problems);
    }

    [Theory]
    [InlineData("Not for you.", "Not for you.")]
    [InlineData(null, "You are not allowed to export this visual.")]
    public async Task AuthorizerDenial_IsForbidden(string? reason, string expected)
    {
        _authorizer = new(_ => VisualExportAuthorization.Deny(reason!));
        var result = await Resolve(Request());
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(expected, result.Error);
        Assert.Null(result.Context);
    }

    [Fact]
    public async Task AllowListAuthorizer_DeniesWhenTheListIsEmpty()
    {
        var context = (await Resolve(Request())).Context!;

        var allow = await new AllowListAuthorizer(TestOptions.Create(o => o.AllowedWorkspaceIds.Add(Ws))).AuthorizeAsync(context, default);
        Assert.True(allow.Allowed);

        var deny = await new AllowListAuthorizer(TestOptions.Create()).AuthorizeAsync(context, default);
        Assert.False(deny.Allowed);
        Assert.Contains("AllowedWorkspaceIds", deny.Reason);
    }
}
