using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Docutracks.Api.Infrastructure;

/// <summary>
/// Posts the forms of the classic Docman UI (ASP.NET MVC) **as the user**, as the browser does when
/// a button is pressed: the same URL, fields and files, the user's cookies, and the anti-forgery
/// token of the form (taken with a GET of a page of the user and kept for a while). So the
/// Docutracks code runs unchanged (DocumentModelBinder, IUiAction.ExecuteAction) and nothing is added
/// to the solution. The price: it depends on the routes and on a few marks of the HTML responses.
/// </summary>
public sealed partial class ClassicClient(HttpClient http, UserContext user, IOptions<ApiOptions> options, IMemoryCache cache, ILogger<ClassicClient> log)
{
    public const string HttpClientName = V1Client.HttpClientName;

    private readonly ApiOptions opt = options.Value;

    /// <summary>`/{culture}/{controller}/{group}/...` as the MVC routes of Docman.</summary>
    public string Url(string controller, int groupId, string rest) =>
        $"{opt.Classic.Culture.Trim('/')}/{controller}/{groupId}/{rest.TrimStart('/')}";

    /* ---------- the result of a post ---------- */

    public sealed record PostResult(bool Success, int? EntityId, string? Message, string Html);

    /// <summary>
    /// A form of DocumentController.DocumentAction. Success: a redirect to `uiactionsuccess` (the
    /// controller's RedirectToUiAction, with `Id` and `aCtx`). Failure: the page again, with the
    /// message in `.uiaction-messages .errormessage`.
    /// </summary>
    public async Task<PostResult> Post(string path, IReadOnlyDictionary<string, List<string>> fields,
        IReadOnlyList<FormFile>? files, string tokenPage, CancellationToken ct, params string[] fallbackPages)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await Token([tokenPage, .. fallbackPages], refresh: attempt > 0, ct);
            using var req = Request(HttpMethod.Post, path, token.Cookie);
            req.Content = Content(fields, files, token.Value);
            using var res = await Send(req, ct);
            var html = await res.Content.ReadAsStringAsync(ct);
            var location = res.Headers.Location?.ToString();
            if (IsLogin(res, location, html)) throw ApiException.Unauthorized();
            if (res.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.MovedPermanently)
                return Redirected(location);
            if (IsAntiForgeryFailure(res, html) && attempt == 0)
            {
                log.LogInformation("Anti-forgery token refused for {Path}; taking a new one", path);
                continue;
            }
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Classic POST {Path}: {Status}", path, (int)res.StatusCode);
                return new PostResult(false, null, ErrorMessage(html) ?? $"Το Docutracks απάντησε με σφάλμα ({(int)res.StatusCode}).", html);
            }
            return new PostResult(false, null, ErrorMessage(html), html);
        }
    }

    /// <summary>A form whose answer is JSON or HTML to read (no redirect expected).</summary>
    public async Task<string> PostRead(string path, IReadOnlyDictionary<string, List<string>> fields, string? tokenPage, CancellationToken ct, params string[] fallbackPages)
    {
        for (var attempt = 0; ; attempt++)
        {
            Antiforgery? token = tokenPage is null ? null : await Token([tokenPage, .. fallbackPages], refresh: attempt > 0, ct);
            using var req = Request(HttpMethod.Post, path, token?.Cookie);
            req.Content = Content(fields, null, token?.Value);
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            using var res = await Send(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (IsLogin(res, res.Headers.Location?.ToString(), text)) throw ApiException.Unauthorized();
            if (tokenPage is not null && IsAntiForgeryFailure(res, text) && attempt == 0) continue;
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Classic POST {Path}: {Status}", path, (int)res.StatusCode);
                throw new ApiException(ErrorMessage(text) ?? $"Το Docutracks απάντησε με σφάλμα ({(int)res.StatusCode}).");
            }
            return text;
        }
    }

    public async Task<JsonNode?> PostJson(string path, IReadOnlyDictionary<string, List<string>> fields, CancellationToken ct)
    {
        var text = await PostRead(path, fields, null, ct);
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ApiException("Το Docutracks δεν απάντησε όπως αναμενόταν.");
        }
    }

    internal static PostResult Redirected(string? location)
    {
        // e.g. /el/Document/2/UiViewDoc/104480?aCtx={Code:'UiNew',ExecuteResult:{EntityId:'104480',Success:true,...,UiMessage:''}}
        var decoded = WebUtility.UrlDecode(location ?? "");
        int? id = null;
        var m = EntityIdInContext().Match(decoded);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var fromCtx)) id = fromCtx;
        else if (IdInUrl().Match(decoded) is { Success: true } u && int.TryParse(u.Groups[1].Value, out var fromUrl)) id = fromUrl;
        var ok = !SuccessFalse().IsMatch(decoded);
        var message = UiMessage().Match(decoded) is { Success: true } um && um.Groups[1].Value.Length > 0 ? um.Groups[1].Value.Replace("\\'", "'") : null;
        return new PostResult(ok, id, message, "");
    }

    /// <summary>The first `.errormessage` of the page (UIActionsHelper.UIActionExecMessagesForEntities), as text.</summary>
    internal static string? ErrorMessage(string html)
    {
        var m = ErrorDiv().Match(html);
        if (!m.Success) m = DisabledMessage().Match(html);
        if (!m.Success) return null;
        var text = WebUtility.HtmlDecode(Tags().Replace(m.Groups[1].Value, " "));
        text = Spaces().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool IsLogin(HttpResponseMessage res, string? location, string body) =>
        res.StatusCode == HttpStatusCode.Unauthorized
        || (location?.Contains("/Account/LogOn", StringComparison.OrdinalIgnoreCase) ?? false)
        || (res.IsSuccessStatusCode && body.Contains("id=\"loginForm\"", StringComparison.OrdinalIgnoreCase));

    private static bool IsAntiForgeryFailure(HttpResponseMessage res, string body) =>
        !res.IsSuccessStatusCode && body.Contains("AntiForgery", StringComparison.OrdinalIgnoreCase);

    /* ---------- anti-forgery token ---------- */

    public sealed record Antiforgery(string Value, string? Cookie);

    /// <summary>
    /// The `__RequestVerificationToken` of a form page of the user (MVC tokens are bound to the user
    /// and the cookie token, not to the page), with the anti-forgery cookie the page may have set.
    /// </summary>
    private async Task<Antiforgery> Token(string[] pages, bool refresh, CancellationToken ct)
    {
        var key = "af:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user.Cookie ?? user.Authorization ?? "")));
        if (!refresh && cache.TryGetValue(key, out Antiforgery? cached) && cached is not null) return cached;
        string? failure = null;
        foreach (var page in pages)
        {
            using var req = Request(HttpMethod.Get, page, null);
            using var res = await Send(req, ct);
            var html = await res.Content.ReadAsStringAsync(ct);
            if (IsLogin(res, res.Headers.Location?.ToString(), html)) throw ApiException.Unauthorized();
            var value = TokenInput(html);
            if (value is null)
            {
                failure ??= res.IsSuccessStatusCode ? ErrorMessage(html) : $"Το Docutracks απάντησε με σφάλμα ({(int)res.StatusCode}).";
                log.LogInformation("No anti-forgery token in {Page} ({Status})", page, (int)res.StatusCode);
                continue;
            }
            var cookie = res.Headers.TryGetValues("Set-Cookie", out var set)
                ? set.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith("__RequestVerificationToken", StringComparison.Ordinal))
                : null;
            var token = new Antiforgery(value, cookie);
            cache.Set(key, token, TimeSpan.FromMinutes(opt.Classic.TokenMinutes));
            return token;
        }
        throw new ApiException(failure ?? "Η φόρμα του Docutracks δεν βρέθηκε.");
    }

    internal static string? TokenInput(string html)
    {
        foreach (Match input in InputTag().Matches(html))
        {
            var tag = input.Value;
            if (!tag.Contains("__RequestVerificationToken", StringComparison.Ordinal)) continue;
            var v = ValueAttr().Match(tag);
            if (v.Success) return WebUtility.HtmlDecode(v.Groups[2].Value);
        }
        return null;
    }

    /* ---------- HTTP ---------- */

    public sealed record FormFile(string Field, string FileName, string Path);

    private static HttpContent Content(IReadOnlyDictionary<string, List<string>> fields, IReadOnlyList<FormFile>? files, string? token)
    {
        var pairs = fields.SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string>(kv.Key, v ?? ""))).ToList();
        if (token is not null) pairs.Add(new("__RequestVerificationToken", token));
        if (files is not { Count: > 0 }) return new FormUrlEncodedContent(pairs);

        var multipart = new MultipartFormDataContent();
        foreach (var (k, v) in pairs) multipart.Add(new StringContent(v, System.Text.Encoding.UTF8), Quote(k));
        foreach (var f in files)
        {
            var part = new StreamContent(File.OpenRead(f.Path));
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(part, Quote(f.Field), Quote(f.FileName));
        }
        return multipart;
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";

    private HttpRequestMessage Request(HttpMethod method, string path, string? extraCookie)
    {
        var req = new HttpRequestMessage(method, new Uri(new Uri(opt.V1.BaseUrl), path.TrimStart('/')));
        req.Headers.Accept.ParseAdd("text/html,application/json;q=0.9,*/*;q=0.8");
        req.Headers.AcceptLanguage.ParseAdd("el-GR");
        var cookie = Merge(user.Cookie, extraCookie);
        if (!string.IsNullOrEmpty(cookie)) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (!string.IsNullOrEmpty(user.Authorization)) req.Headers.TryAddWithoutValidation("Authorization", user.Authorization);
        return req;
    }

    /// <summary>The browser's cookies, with the anti-forgery cookie the token page set (replacing one of the same name).</summary>
    internal static string? Merge(string? cookies, string? extra)
    {
        if (string.IsNullOrEmpty(extra)) return cookies;
        var name = extra.Split('=')[0];
        var kept = (cookies ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => !c.StartsWith(name + "=", StringComparison.Ordinal));
        return string.Join("; ", kept.Append(extra));
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(req, ct);
        }
        catch (HttpRequestException e)
        {
            log.LogError(e, "Docutracks unreachable: {Uri}", req.RequestUri);
            throw new ApiException("Το Docutracks δεν είναι διαθέσιμο αυτή τη στιγμή.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("Το Docutracks άργησε να απαντήσει.");
        }
    }

    [GeneratedRegex(@"EntityId:'(\d+)'")] private static partial Regex EntityIdInContext();
    [GeneratedRegex(@"(?:/UiViewDoc/|[?&]Id=)(\d+)", RegexOptions.IgnoreCase)] private static partial Regex IdInUrl();
    [GeneratedRegex(@"Success:false")] private static partial Regex SuccessFalse();
    [GeneratedRegex(@"UiMessage:'((?:[^'\\]|\\.)*)'")] private static partial Regex UiMessage();
    [GeneratedRegex(@"class=['""]errormessage['""][^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex ErrorDiv();
    [GeneratedRegex(@"class=['""]uiactionbutton-disabled-message['""][^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex DisabledMessage();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"<input\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex InputTag();
    [GeneratedRegex(@"\bvalue=(['""])(.*?)\1", RegexOptions.IgnoreCase)] private static partial Regex ValueAttr();
}
