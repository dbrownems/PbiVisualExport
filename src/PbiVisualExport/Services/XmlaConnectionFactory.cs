using System.Data.Common;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Extensions.Options;
using PbiVisualExport.Auth;
using AsAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace PbiVisualExport.Services;

/// <summary>
/// Opens ADOMD.NET connections to a semantic model through the workspace XMLA endpoint.
/// XMLA only accepts <b>names</b>: Data Source = endpoint/{workspace name},
/// Initial Catalog = {semantic model name} (GUIDs are rejected).
/// </summary>
internal sealed class XmlaConnectionFactory
{
    private readonly XmlaTokenProvider _tokens;
    private readonly PbiVisualExportOptions _options;

    public XmlaConnectionFactory(XmlaTokenProvider tokens, IOptions<PbiVisualExportOptions> options)
    {
        _tokens = tokens;
        _options = options.Value;
    }

    public string DataSource(string workspaceName) => $"{_options.XmlaEndpoint.TrimEnd('/')}/{workspaceName}";

    internal string BuildConnectionString(string workspaceName, string datasetName, EffectiveIdentity? identity)
    {
        var csb = new DbConnectionStringBuilder
        {
            ["Data Source"] = DataSource(workspaceName),
            ["Initial Catalog"] = datasetName,
        };
        if (!string.IsNullOrWhiteSpace(identity?.UserName)) csb["EffectiveUserName"] = identity.UserName;
        if (identity?.Roles is { Count: > 0 } roles) csb["Roles"] = string.Join(",", roles);
        if (!string.IsNullOrEmpty(identity?.CustomData)) csb["CustomData"] = identity.CustomData;
        return csb.ConnectionString;
    }

    public async Task<AdomdConnection> OpenAsync(string workspaceName, string datasetName, EffectiveIdentity? identity, CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(ct);

        var conn = new AdomdConnection(BuildConnectionString(workspaceName, datasetName, identity))
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
