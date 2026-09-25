using Microsoft.AspNetCore.Http;

namespace PbiVisualExport.Services;

/// <summary>
/// The export request can't be satisfied as sent (e.g. a field is in neither the semantic
/// model nor the report's measures, or isn't one of the visual's fields). Returned to the
/// client with <see cref="StatusCode"/> (400, or 403 when the request asks for more than
/// the visual shows) and the message.
/// </summary>
internal sealed class ExportRequestException : Exception
{
    public ExportRequestException(string message, int statusCode = StatusCodes.Status400BadRequest) : base(message)
        => StatusCode = statusCode;

    public int StatusCode { get; }
}