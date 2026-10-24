using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace Docutracks.Api.Infrastructure;

/// <summary>The caller of this request: the credentials to forward and who they belong to.</summary>
public sealed class UserContext
{
    public string? Cookie { get; set; }
    public string? Authorization { get; set; }
    public CurrentUser? User { get; set; }

    public CurrentUser Required => User ?? throw ApiException.Unauthorized();
}

public sealed record CurrentUser(int Id, string UserName, string FullName);

/// <summary>Who the credentials of the request belong to, as v1 sees them (`currentUserAsLogin`).</summary>
public interface IIdentityResolver
{
    Task<CurrentUser?> Resolve(UserContext ctx, CancellationToken ct);
}

public sealed class V1IdentityResolver(IHttpClientFactory httpFactory, IMemoryCache cache, IOptions<ApiOptions> options, ILogger<V1IdentityResolver> log)
    : IIdentityResolver
{
    public async Task<CurrentUser?> Resolve(UserContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.Cookie) && string.IsNullOrEmpty(ctx.Authorization)) return null;
        var key = "id:" + Hash(ctx.Cookie + "\n" + ctx.Authorization);
        if (cache.TryGetValue(key, out CurrentUser? hit)) return hit;

        var http = httpFactory.CreateClient(V1Client.HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(options.Value.V1.BaseUrl), "services/authentication/currentUserAsLogin"));
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrEmpty(ctx.Cookie)) req.Headers.TryAddWithoutValidation("Cookie", ctx.Cookie);
        if (!string.IsNullOrEmpty(ctx.Authorization)) req.Headers.TryAddWithoutValidation("Authorization", ctx.Authorization);
        try
        {
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var n = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            var id = n?["IdUser"]?.GetValue<int>() ?? 0;
            if (n?["Success"]?.GetValue<bool>() != true || id <= 0) return null;
            var user = new CurrentUser(id, n["Name"]?.GetValue<string>() ?? "", n["Fullname"]?.GetValue<string>() ?? "");
            cache.Set(key, user, TimeSpan.FromSeconds(options.Value.IdentityCacheSeconds));
            return user;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogWarning(e, "Identity check against v1 failed");
            throw new ApiException("Το Docutracks δεν είναι διαθέσιμο αυτή τη στιγμή.");
        }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

/// <summary>The user's role in a group: every call names its GroupId and must have one (SetContextGroup of v1).</summary>
public interface IGroupAccess
{
    Task Ensure(int groupId, CancellationToken ct);
}

public sealed class GroupAccess(IDb db, UserContext ctx, IMemoryCache cache, IHostEnvironment env) : IGroupAccess
{
    private sealed class Diagnosis
    {
        public string? Server { get; set; }
        public string? Database { get; set; }
        public string? UserName { get; set; }
        public int Relations { get; set; }
        public int InGroup { get; set; }
        public string? EnabledInGroup { get; set; }
    }

    public async Task Ensure(int groupId, CancellationToken ct)
    {
        if (groupId <= 0) throw ApiException.BadRequest("Λείπει η υπηρεσία (GroupId).");
        var user = ctx.Required;
        var key = $"role:{user.Id}:{groupId}";
        if (!cache.TryGetValue(key, out bool ok))
        {
            ok = await db.Scalar<int>(
                """
                SELECT COUNT(1) FROM Organization.UserRoleGroupRelationship
                 WHERE IdOrgUser = @UserId AND IdOrgGroup = @GroupId AND Enabled = 1
                """, new { UserId = user.Id, GroupId = groupId }, ct) > 0;
            cache.Set(key, ok, TimeSpan.FromSeconds(60));
        }
        if (!ok)
        {
            if (env.IsDevelopment())
            {
                // Which database the service reads and what it has for this user: helps when the
                // connection string points to another installation than V1:BaseUrl.
                var d = await db.First<Diagnosis>(
                    """
                    SELECT @@SERVERNAME AS Server, DB_NAME() AS [Database],
                           (SELECT TOP 1 UserName FROM Organization.OrgUser WHERE IdOrgUser = @UserId) AS UserName,
                           (SELECT COUNT(1) FROM Organization.UserRoleGroupRelationship WHERE IdOrgUser = @UserId) AS Relations,
                           (SELECT COUNT(1) FROM Organization.UserRoleGroupRelationship WHERE IdOrgUser = @UserId AND IdOrgGroup = @GroupId) AS InGroup,
                           (SELECT CONCAT('min ', ISNULL(CAST(MIN(CAST(Enabled AS int)) AS varchar(5)), 'NULL'), ', max ', ISNULL(CAST(MAX(CAST(Enabled AS int)) AS varchar(5)), 'NULL')) FROM Organization.UserRoleGroupRelationship WHERE IdOrgUser = @UserId AND IdOrgGroup = @GroupId) AS EnabledInGroup
                    """, new { UserId = user.Id, GroupId = groupId }, ct);
                throw ApiException.Forbidden(
                    $"Δεν έχετε ρόλο σε αυτή την υπηρεσία. [v1: χρήστης {user.Id} «{user.UserName}», υπηρεσία {groupId}· " +
                    $"βάση {d?.Server}/{d?.Database}: χρήστης «{d?.UserName ?? "δεν υπάρχει"}», σχέσεις {d?.Relations}, στην υπηρεσία {d?.InGroup} (Enabled: {d?.EnabledInGroup ?? "-"})]");
            }
            throw ApiException.Forbidden();
        }
    }
}

/// <summary>Takes the credentials of the request, resolves the user, refuses anonymous calls with 401.</summary>
public sealed class IdentityMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext http, UserContext ctx, IIdentityResolver resolver)
    {
        if (!http.Request.Path.StartsWithSegments("/v2"))
        {
            await next(http);
            return;
        }
        ctx.Cookie = http.Request.Headers.Cookie.ToString();
        ctx.Authorization = http.Request.Headers.Authorization.ToString();
        ctx.User = await resolver.Resolve(ctx, http.RequestAborted);
        if (ctx.User is null) throw ApiException.Unauthorized();
        await next(http);
    }
}

/// <summary>Every failure becomes the envelope; unexpected ones are logged and get a generic message.</summary>
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log, IHostEnvironment env)
{
    public async Task Invoke(HttpContext http)
    {
        try
        {
            await next(http);
        }
        catch (ApiException e)
        {
            await Write(http, (int)e.Status, e.Message, e.Code);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The browser left; nothing to answer.
        }
        catch (Exception e)
        {
            log.LogError(e, "Unhandled error on {Method} {Path}", http.Request.Method, http.Request.Path);
            var message = e is System.Data.Common.DbException || e.InnerException is System.Data.Common.DbException
                ? "Δεν ήταν δυνατή η ανάγνωση από τη βάση του Docutracks."
                : "Παρουσιάστηκε σφάλμα. Δοκιμάστε ξανά ή ενημερώστε τον διαχειριστή.";
            // Development: the cause too, so a local run shows it without the console.
            if (env.IsDevelopment()) message += " [" + Detail(e) + "]";
            await Write(http, 200, message, "error");
        }
    }

    private static string Detail(Exception e)
    {
        var parts = new List<string>();
        for (var x = e; x is not null && parts.Count < 4; x = x.InnerException) parts.Add($"{x.GetType().Name}: {x.Message}");
        return string.Join(" → ", parts);
    }

    private static async Task Write(HttpContext http, int status, string message, string? code)
    {
        if (http.Response.HasStarted) return;
        http.Response.Clear();
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(Envelope.Fail(message, code), Json.Options);
    }
}
