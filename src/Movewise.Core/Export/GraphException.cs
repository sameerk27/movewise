using System.Net;
using System.Text.Json.Nodes;

namespace Movewise.Core.Export;

/// <summary>An error response from Microsoft Graph.</summary>
public sealed class GraphException(HttpStatusCode status, string code, string message)
    : Exception($"Microsoft Graph returned {(int)status} {code}: {message}")
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;

    public static async Task<GraphException> FromResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var error = JsonNode.Parse(text)?["error"];
            return new GraphException(response.StatusCode,
                error?["code"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Error",
                error?["message"]?.GetValue<string>() ?? text);
        }
        catch (System.Text.Json.JsonException)
        {
            return new GraphException(response.StatusCode, response.ReasonPhrase ?? "Error", text);
        }
    }
}
