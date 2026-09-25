namespace PbiVisualExport.Services;

/// <summary>
/// The export request can't be satisfied as sent (e.g. a field is in neither the semantic
/// model nor the report's measures). Returned to the client as 400 with the message.
/// </summary>
internal sealed class ExportRequestException : Exception
{
    public ExportRequestException(string message) : base(message) { }
}
