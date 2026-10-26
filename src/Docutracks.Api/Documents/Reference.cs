using Microsoft.Extensions.Caching.Memory;

namespace Docutracks.Api.Documents;

/// <summary>Constants of Common.Constants that the v2 endpoints need.</summary>
public static class Roles
{
    public const int Administrator = 1, Director = 2, User = 3, DeputyDirector = 4, SecretarialSupport = 7, CopyVerifier = 8,
        LimitedView = 16, OutgoingRegistrar = 24, IncomingRegistrar = 26, IncomingRegistrarWithFile = 31, Consultor = 42;
}

public static class GroupKinds
{
    /// <summary>Common.Constants.GroupKind.CENTRAL_PROTOCOL.</summary>
    public const int CentralProtocol = 1;
}

/// <summary>Who the user is in the group: roles and what follows from them (ListModel.IsDirector, …).</summary>
public sealed class GroupRole
{
    public int GroupId { get; init; }
    public string GroupName { get; init; } = "";
    public int? GroupKindId { get; init; }
    public int? ParentGroupId { get; init; }
    public bool IsActiveOldProtokollo { get; init; }
    public HashSet<int> Roles { get; init; } = [];
    public bool IsAdministratorUser { get; init; }
    /// <summary>The group's active manager (IsDirectorByUserid), as ListModel.IsDirector.</summary>
    public bool IsDirector { get; init; }
    public bool IsSecretarialSupport => Roles.Contains(Documents.Roles.SecretarialSupport);
    public bool IsProtocolUser => GroupKindId == GroupKinds.CentralProtocol;
    public bool IsConsultor => Roles.Contains(Documents.Roles.Consultor);
    public bool Has(int role) => Roles.Contains(role);
}

public sealed class KindRow
{
    public int IdDocumentKind { get; set; }
    public int? ParentId { get; set; }
    public string? Title { get; set; }
    public string? Code { get; set; }
    public bool? EnabledForIncoming { get; set; }
    public bool? EnabledForOutgoing { get; set; }
    public bool? EnabledForSxedio { get; set; }
    public int? IdDocumentKindMetadata { get; set; }
}

public sealed class CategoryRow
{
    public int IdCategory { get; set; }
    public int? IdParentCategory { get; set; }
    public string? Title { get; set; }
    public bool? IsDiaugeia { get; set; }
}

public sealed class StatusRow
{
    public int IdStatus { get; set; }
    public string? StatusCode { get; set; }
    public string? Title { get; set; }
    public bool IsFinalStatus { get; set; }
}

public interface IReference
{
    Task<GroupRole> Role(int userId, int groupId, CancellationToken ct);
    Task<IReadOnlyDictionary<int, StatusRow>> Statuses(CancellationToken ct);
    Task<IReadOnlyList<Option>> Titles(string table, string idColumn, string resources, CancellationToken ct, string? where = null);
    Task<IReadOnlyList<KindRow>> Kinds(CancellationToken ct);
    Task<IReadOnlyList<CategoryRow>> Categories(CancellationToken ct);
    Task<string?> UserName(int userId, CancellationToken ct);
    Task<string?> GroupName(int groupId, CancellationToken ct);
}

public sealed class Reference(IDb db, IMemoryCache cache, IOptions<ApiOptions> options) : IReference
{
    private TimeSpan Ttl => TimeSpan.FromSeconds(options.Value.ReferenceCacheSeconds);

    private Task<T> Cached<T>(string key, Func<Task<T>> load, TimeSpan? ttl = null) =>
        cache.GetOrCreateAsync(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = ttl ?? Ttl;
            return load();
        })!;

    private sealed class GroupRow
    {
        public int IdOrgGroup { get; set; }
        public string? Name { get; set; }
        public int? IdOrgGroupKind { get; set; }
        public int? IdParentOrgGroup { get; set; }
        public bool? IsActiveOldProtokollo { get; set; }
        public int? IdActiveManager { get; set; }
    }

    private sealed class RoleRow
    {
        public int IdOrgRole { get; set; }
    }

    public Task<GroupRole> Role(int userId, int groupId, CancellationToken ct) =>
        Cached($"grouprole:{userId}:{groupId}", async () =>
        {
            var g = await db.First<GroupRow>(
                "SELECT IdOrgGroup, Name, IdOrgGroupKind, IdParentOrgGroup, IsActiveOldProtokollo, IdActiveManager FROM Organization.OrgGroup WHERE IdOrgGroup = @groupId",
                new { groupId }, ct) ?? throw ApiException.Forbidden("Η υπηρεσία δεν βρέθηκε.");
            var roles = (await db.Query<RoleRow>(
                "SELECT ur.IdOrgRole FROM Organization.UserRoleGroupRelationship ur WHERE ur.IdOrgUser = @userId AND ur.IdOrgGroup = @groupId AND EXISTS (SELECT 1 FROM Organization.OrgRole r WHERE r.IdOrgRole = ur.IdOrgRole AND r.IsAssignable = 1)",
                new { userId, groupId }, ct)).Select(r => r.IdOrgRole).ToHashSet();
            var isAdmin = await db.Scalar<bool?>("SELECT IsAdministrator FROM Organization.OrgUser WHERE IdOrgUser = @userId", new { userId }, ct) ?? false;
            return new GroupRole
            {
                GroupId = groupId,
                GroupName = g.Name ?? "",
                GroupKindId = g.IdOrgGroupKind,
                ParentGroupId = g.IdParentOrgGroup,
                IsActiveOldProtokollo = g.IsActiveOldProtokollo ?? false,
                Roles = roles,
                IsAdministratorUser = isAdmin,
                // OrganizationEntityService.IsDirectorByUserid: the group's active manager, with a role in it.
                IsDirector = g.IdActiveManager == userId && roles.Count > 0,
            };
        }, TimeSpan.FromSeconds(60));

    public Task<IReadOnlyDictionary<int, StatusRow>> Statuses(CancellationToken ct) =>
        Cached("statuses", async () =>
            (IReadOnlyDictionary<int, StatusRow>)(await db.Query<StatusRow>(
                $"SELECT s.IdStatus, s.StatusCode, s.IsFinalStatus, {Sql.Title("[Workflow].[StatusCodeResources]", "s.IdStatus")} AS Title FROM Workflow.Status s",
                ct: ct)).ToDictionary(s => s.IdStatus));

    private sealed class TitleRow
    {
        public int Id { get; set; }
        public string? Title { get; set; }
    }

    /// <summary>Id and Greek title of an active classifier (`IsActive = 1` when the table has it, via `where`).</summary>
    public Task<IReadOnlyList<Option>> Titles(string table, string idColumn, string resources, CancellationToken ct, string? where = null) =>
        Cached($"titles:{table}:{where}", async () =>
            (IReadOnlyList<Option>)(await db.Query<TitleRow>(
                $"SELECT t.{idColumn} AS Id, {Sql.Title(resources, "t." + idColumn)} AS Title FROM {table} t {(where is null ? "" : "WHERE " + where)} ORDER BY t.{idColumn}",
                ct: ct)).Select(r => new Option(r.Id, r.Title ?? r.Id.ToString())).ToList());

    public Task<IReadOnlyList<KindRow>> Kinds(CancellationToken ct) =>
        Cached("kinds", async () => (IReadOnlyList<KindRow>)await db.Query<KindRow>(
            $"""
            SELECT k.IdDocumentKind, k.ParentId, k.Code, k.EnabledForIncoming, k.EnabledForOutgoing, k.EnabledForSxedio, k.IdDocumentKindMetadata,
                   {Sql.Title("[Document].[DocumentKindTitleResources]", "k.IdDocumentKind")} AS Title
              FROM Document.DocumentKind k
             WHERE k.IsActive = 1 AND (k.ValidUntil IS NULL OR k.ValidUntil > GETDATE())
            """, ct: ct));

    public Task<IReadOnlyList<CategoryRow>> Categories(CancellationToken ct) =>
        Cached("categories", async () => (IReadOnlyList<CategoryRow>)await db.Query<CategoryRow>(
            $"""
            SELECT c.IdCategory, c.IdParentCategory, c.IsDiaugeia, {Sql.Title("[Document].[CategoryTitleResources]", "c.IdCategory")} AS Title
              FROM Document.Category c
             WHERE c.IsActive = 1 AND (c.ValidUntil IS NULL OR c.ValidUntil > GETDATE())
            """, ct: ct));

    public Task<string?> UserName(int userId, CancellationToken ct) =>
        Cached($"user:{userId}", () => db.Scalar<string>(
            "SELECT LTRIM(RTRIM(ISNULL(Surname, '') + ' ' + ISNULL(Name, ''))) FROM Organization.OrgUser WHERE IdOrgUser = @userId", new { userId }, ct));

    public Task<string?> GroupName(int groupId, CancellationToken ct) =>
        Cached($"group:{groupId}", () => db.Scalar<string>("SELECT Name FROM Organization.OrgGroup WHERE IdOrgGroup = @groupId", new { groupId }, ct));
}

public static class Trees
{
    /// <summary>A flat list with parents as a tree of options, children sorted by title.</summary>
    public static List<TreeOption> Build<T>(IEnumerable<T> rows, Func<T, int> id, Func<T, int?> parent, Func<T, string> title, Func<T, bool>? hasForm = null)
    {
        var list = rows.ToList();
        var ids = list.Select(id).ToHashSet();
        var byParent = list.ToLookup(r => parent(r) is int p && ids.Contains(p) ? p : (int?)null);
        List<TreeOption> Children(int? p) => byParent[p]
            .OrderBy(title, StringComparer.CurrentCulture)
            .Select(r =>
            {
                var kids = Children(id(r));
                return new TreeOption
                {
                    Id = id(r),
                    Title = title(r),
                    Selectable = kids.Count == 0 ? null : false,
                    HasForm = hasForm?.Invoke(r) == true ? true : null,
                    Children = kids.Count == 0 ? null : kids,
                };
            }).ToList();
        return Children(null);
    }

    /// <summary>A tree as indented options (filters show trees this way).</summary>
    public static List<FilterOption> Flatten(IEnumerable<TreeOption> nodes, int depth = 0) =>
        nodes.SelectMany(n => new[] { new FilterOption { Id = n.Id, Label = n.Title, Depth = depth == 0 ? null : depth } }
            .Concat(Flatten(n.Children ?? [], depth + 1))).ToList();
}
