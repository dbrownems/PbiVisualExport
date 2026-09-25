namespace PbiVisualExport.SampleHost;

/// <summary>The sample's own settings (section <c>PowerBI</c>): which workspace/report it embeds.</summary>
public sealed class PowerBIOptions
{
    public const string SectionName = "PowerBI";

    /// <summary>Required: the only workspace the sample embeds from (and may export from).</summary>
    public string WorkspaceId { get; set; } = "";

    /// <summary>Optional: the report selected on first load (otherwise the first report in the workspace).</summary>
    public string ReportId { get; set; } = "";
}
