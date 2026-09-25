using System.Data.Common;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Extensions.Options;
using PbiVisualExport.Web.Auth;
using PbiVisualExport.Web.Options;
using AsAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace PbiVisualExport.Web.Services;

/// <summary>
/// Opens ADOMD.NET connections to a semantic model through the workspace XMLA endpoint.
/// XMLA only accepts <b>names</b>: Data Source = endpoint/{workspace name},
/// Initial Catalog = {semantic model name} (GUIDs are rejected).
/// </summary>
public sealed class XmlaConnectionFactory
{
    private readonly XmlaTokenProvider _tokens;
    private readonly PowerBIOptions _pbi;

    public XmlaConnectionFactory(XmlaTokenProvider tokens, IOptions<PowerBIOptions> pbi)
    {
        _tokens = tokens;
        _pbi = pbi.Value;
    }

    public string DataSource(string workspaceName) => $"{_pbi.XmlaEndpoint.TrimEnd('/')}/{workspaceName}";

    public async Task<AdomdConnection> OpenAsync(string workspaceName, string datasetName, CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(ct);

        var csb = new DbConnectionStringBuilder
        {
            ["Data Source"] = DataSource(workspaceName),
            ["Initial Catalog"] = datasetName,
        };
        var conn = new AdomdConnection(csb.ConnectionString)
        {
            AccessToken = new AsAccessToken(token.Token, token.ExpiresOn, null),
            // Long-running exports can outlive the token; ADOMD asks for a fresh one.
            OnAccessTokenExpired = _ =>
            {
                var t = _tokens.GetTokenAsync(CancellationToken.None).GetAwaiter().GetResult();
                return new AsAccessToken(t.Token, t.ExpiresOn, null);
            },
        };

        try
        {
            await Task.Run(conn.Open, ct);
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }
}
