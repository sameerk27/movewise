using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;

namespace Movewise.M365.Graph;

/// <summary>
/// A small Microsoft Graph client that works with raw JSON, which is what the engine normalizes and compares.
/// Retries throttled and transient responses, honouring Retry-After.
/// </summary>
public sealed class GraphClient : IGraphWriter
{
    const int MaxAttempts = 6;

    static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://graph.microsoft.com/"),
        Timeout = TimeSpan.FromSeconds(100),
    };

    readonly Func<CancellationToken, Task<string>> _getToken;
    readonly Uri _baseAddress;

    /// <param name="baseAddress">
    /// Another API that speaks the same JSON and paging as Graph, such as the Defender for Endpoint API; Graph when null.
    /// </param>
    public GraphClient(Func<CancellationToken, Task<string>> getToken, Uri? baseAddress = null)
    {
        _getToken = getToken;
        _baseAddress = baseAddress ?? Http.BaseAddress!;
    }

    public async Task<JsonObject> GetObjectAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        return (JsonObject)(await ReadJsonAsync(response, ct))!;
    }

    /// <summary>Reads every page of a collection.</summary>
    public async Task<IReadOnlyList<JsonObject>> GetCollectionAsync(string path, CancellationToken ct = default)
    {
        var items = new List<JsonObject>();
        string? next = path;
        while (next is not null)
        {
            using var response = await SendAsync(HttpMethod.Get, next, null, ct);
            var page = (JsonObject)(await ReadJsonAsync(response, ct))!;
            if (page["value"] is JsonArray value)
                items.AddRange(value.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()));
            next = page["@odata.nextLink"]?.GetValue<string>();
        }
        return items;
    }

    public async Task<JsonObject?> PostAsync(string path, JsonNode body, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, ct);
        return await ReadJsonAsync(response, ct) as JsonObject;
    }

    public async Task PatchAsync(string path, JsonNode body, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Patch, path, body, ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, path, null, ct);
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathOrUrl, JsonNode? body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(_baseAddress, pathOrUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _getToken(ct));
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            var response = await Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return response;

            if (IsRetryable(method, response.StatusCode) && attempt < MaxAttempts)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                response.Dispose();
                await Task.Delay(delay, ct);
                continue;
            }

            using (response)
                throw await GraphException.FromResponseAsync(response, ct);
        }
    }

    /// <summary>
    /// Reads retry on any transient error. A write only retries when Graph says it didn't process the request
    /// (throttled or unavailable); after a gateway error the object may exist, and retrying could create it twice.
    /// </summary>
    static bool IsRetryable(HttpMethod method, HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
        || (method == HttpMethod.Get && status is HttpStatusCode.GatewayTimeout or HttpStatusCode.BadGateway);

    static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct);
    }
}
