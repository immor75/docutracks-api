using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Docutracks.Api.Infrastructure;

/// <summary>
/// Calls the existing Docutracks API (`/services/...`) **as the user**: the browser's cookies and Authorization header go along, so v1 checks the
/// rights exactly as today.
/// </summary>
public sealed class V1Client(HttpClient http, UserContext user, IOptions<ApiOptions> options, ILogger<V1Client> log)
{
    public const string HttpClientName = "v1";

    private readonly ApiOptions opt = options.Value;

    /// <summary>GET or POST with a JSON body; returns the JSON of the response.</summary>
    public async Task<JsonNode?> Json(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var req = Request(method, path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Infrastructure.Json.Options), Encoding.UTF8, "application/json");
        using var res = await Send(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw ApiException.Unauthorized();
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("v1 {Method} {Path}: {Status} {Body}", method, path, (int)res.StatusCode, Truncate(text));
            throw new ApiException(ErrorFrom(text) ?? $"Το Docutracks απάντησε με σφάλμα ({(int)res.StatusCode}).");
        }
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            log.LogWarning("v1 {Path}: not JSON: {Body}", path, Truncate(text));
            throw new ApiException("Το Docutracks δεν απάντησε όπως αναμενόταν.");
        }
    }

    public Task<T> Json<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default) =>
        Json(method, path, body, ct).ContinueWith(t => t.Result.Deserialize<T>(Infrastructure.Json.Options)!, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    /// <summary>A file of v1 (e.g. `/services/file/get/{id}`), streamed.</summary>
    public async Task<HttpResponseMessage> Stream(string path, CancellationToken ct = default)
    {
        var req = Request(HttpMethod.Get, path);
        var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw ApiException.Unauthorized();
        return res;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, new Uri(new Uri(opt.V1.BaseUrl), path.TrimStart('/')));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("el-GR"));
        if (!string.IsNullOrEmpty(user.Cookie)) req.Headers.TryAddWithoutValidation("Cookie", user.Cookie);
        if (!string.IsNullOrEmpty(user.Authorization)) req.Headers.TryAddWithoutValidation("Authorization", user.Authorization);
        return req;
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(req, ct);
        }
        catch (HttpRequestException e)
        {
            log.LogError(e, "v1 unreachable: {Uri}", req.RequestUri);
            throw new ApiException("Το Docutracks δεν είναι διαθέσιμο αυτή τη στιγμή.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("Το Docutracks άργησε να απαντήσει.");
        }
    }

    /// <summary>The message of a v1 failure: ServiceStack ResponseStatus or the ErrorInfo of the services.</summary>
    internal static string? ErrorFrom(string text)
    {
        try
        {
            var n = JsonNode.Parse(text);
            return n?["ResponseStatus"]?["Message"]?.GetValue<string>()
                   ?? n?["ErrorInfo"]?["ErrorMessage"]?.GetValue<string>()
                   ?? n?["ErrorInfo"]?["ExceptionMessage"]?.GetValue<string>()
                   ?? n?["Message"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] + "…" : s;
}
