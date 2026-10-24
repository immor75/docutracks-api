using Microsoft.AspNetCore.Http.Extensions;

namespace Docutracks.Api.Folders;

/// <summary>
/// «Υποθέσεις» under the same base as the documents: the FoldersModule already has its API v2
/// (`/services/v2/folders/*`, `/services/v2/folder-types/*`). These routes forward the call as is,
/// as the user, so the client has one base (`/api/v2/`) for both sections and the module keeps its
/// own rules. Calls of v1 (`/services/folder/*`, authentication, organisation) stay on `/services`.
/// </summary>
public static class FoldersEndpoints
{
    public static IServiceCollection AddFolders(this IServiceCollection s) => s.AddScoped<FoldersProxy>();

    public static void MapFolders(this RouteGroupBuilder v2)
    {
        v2.Map("/folders/{**rest}", (string? rest, HttpContext http, FoldersProxy p) => p.Forward(http, "services/v2/folders/" + rest));
        v2.Map("/folder-types/{**rest}", (string? rest, HttpContext http, FoldersProxy p) => p.Forward(http, "services/v2/folder-types/" + rest));
    }
}

public sealed class FoldersProxy(IHttpClientFactory factory, UserContext user, IOptions<ApiOptions> options, ILogger<FoldersProxy> log)
{
    private static readonly HashSet<string> SkipResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Connection", "Keep-Alive", "Set-Cookie", "Server", "X-Powered-By", "X-AspNet-Version",
    };

    public async Task Forward(HttpContext http, string path)
    {
        var target = new Uri(new Uri(options.Value.V1.BaseUrl), path + http.Request.QueryString);
        using var req = new HttpRequestMessage(new HttpMethod(http.Request.Method), target);
        if (http.Request.ContentLength > 0 || http.Request.Headers.TransferEncoding.Count > 0)
        {
            req.Content = new StreamContent(http.Request.Body);
            if (http.Request.ContentType is { } type) req.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        req.Headers.TryAddWithoutValidation("Accept", http.Request.Headers.Accept.ToString());
        req.Headers.TryAddWithoutValidation("Accept-Language", "el-GR");
        if (!string.IsNullOrEmpty(user.Cookie)) req.Headers.TryAddWithoutValidation("Cookie", user.Cookie);
        if (!string.IsNullOrEmpty(user.Authorization)) req.Headers.TryAddWithoutValidation("Authorization", user.Authorization);

        HttpResponseMessage res;
        try
        {
            res = await factory.CreateClient(V1Client.HttpClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
        }
        catch (HttpRequestException e)
        {
            log.LogError(e, "Folders proxy: {Url}", target);
            throw new ApiException("Το Docutracks δεν είναι διαθέσιμο αυτή τη στιγμή.");
        }
        using (res)
        {
            http.Response.StatusCode = (int)res.StatusCode;
            foreach (var h in res.Headers.Concat(res.Content.Headers))
                if (!SkipResponseHeaders.Contains(h.Key)) http.Response.Headers[h.Key] = h.Value.ToArray();
            await res.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        }
    }
}
