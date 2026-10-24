using System.Text.Json;
using System.Text.Json.Nodes;

namespace Docutracks.Api.Documents;

/// <summary>
/// `POST search`: the list of a folder, from the real ListModel criteria through v1
/// `POST /services/documents/documentslistsearch/` (DashboardSearchRequest, ForList), mapped to `DocumentRow`.
/// </summary>
public sealed class SearchService(V1Client v1, FiltersService filters, IReference reference, ISettings settings, IDb db, UserContext user)
{
    public async Task<SearchDocumentsPayload> Search(SearchDocumentsRequest req, CancellationToken ct)
    {
        var key = FolderKey.Parse(req.FolderKey);
        var ctx = await filters.Context(req.GroupId, ct);
        if (!FolderCatalog.Keys(FolderCatalog.Build(ctx)).Contains(key.ToString()))
            throw ApiException.BadRequest("Ο φάκελος δεν είναι διαθέσιμος σε αυτή την υπηρεσία.");

        var size = Math.Clamp(req.PageSize, 1, 100);
        var page = Math.Max(1, req.PageNumber);
        var (from, to) = PeriodRange(req.Period, DateTime.Today);
        var body = ToV1(req.GroupId, key, req.Search, from, to, req.Criteria);
        body["PageNumber"] = page;
        body["PageSize"] = size;
        body["IncludeCount"] = true;
        body["ForList"] = true;

        var res = await v1.Json(HttpMethod.Post, "services/documents/documentslistsearch/", body, ct)
                  ?? throw new ApiException("Η αναζήτηση δεν επέστρεψε αποτέλεσμα.");
        if (res["Success"]?.GetValue<bool>() == false)
            throw new ApiException(res["ErrorInfo"]?["ErrorMessage"]?.GetValue<string>() ?? "Η αναζήτηση απέτυχε.");
        var payload = res["Payload"];
        var statuses = await reference.Statuses(ct);
        var userId = user.Required.Id;
        var rows = (payload?["Data"]?.AsArray() ?? [])
            .Where(n => n is not null)
            .Select(n => MapRow(n!, userId, statuses))
            .ToList();
        await FillNextToSign(rows, ct);

        var pageNode = payload?["Page"];
        var total = pageNode?["TotalElements"]?.GetValue<int>() ?? rows.Count;
        return new SearchDocumentsPayload
        {
            Data = rows,
            Page = new PageInfo(size, page, total),
            Columns = await ColumnsFor(key, ct),
            Period = new AppliedPeriod(Json.Day(from), Json.Day(to ?? DateTime.Today)!),
        };
    }

    /// <summary>The DashboardSearchRequest of a folder, search, period and criteria (also used by export).</summary>
    internal static JsonObject ToV1(int groupId, FolderKey key, SearchText? search, DateTime? from, DateTime? to, Dictionary<string, JsonElement>? criteria)
    {
        var o = new JsonObject
        {
            ["GroupId"] = groupId,
            ["GroupVisibility"] = (int)key.Visibility,
            ["FolderType"] = key.FolderType,
            ["CreationStartDate"] = from?.ToString("yyyy-MM-dd"),
            ["CreationEndDate"] = to?.ToString("yyyy-MM-dd"),
        };
        if (key.DocType != 0) o["DocTypes"] = new JsonArray(key.DocType);
        if (search is { Text.Length: > 0 } && search.Type > 0)
        {
            o["Search"] = search.Text.Trim();
            o["SelectedSearchType"] = search.Type;
        }
        foreach (var (k, v) in criteria ?? [])
        {
            switch (k)
            {
                case "SendDate":
                    o["SendStartDate"] = Json.ParseDay(Str(v, "From"))?.ToString("yyyy-MM-dd");
                    o["SendEndDate"] = Json.ParseDay(Str(v, "To"))?.ToString("yyyy-MM-dd");
                    break;
                case "ActionExpiration":
                    if (Ints(v).FirstOrDefault() is > 0 and var range) o["SelectedActionExpirationDateRange"] = range;
                    break;
                default:
                    if (V1Lists.TryGetValue(k, out var name))
                    {
                        var ids = Ints(v);
                        if (ids.Count > 0) o[name] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray());
                    }
                    break; // Unknown keys are ignored (contract).
            }
        }
        return o;
    }

    /// <summary>Filter keys of v2 → list properties of DashboardSearchRequest.</summary>
    private static readonly Dictionary<string, string> V1Lists = new()
    {
        ["Status"] = "Status", ["Kinds"] = "DocKinds", ["Categories"] = "Categories", ["Origin"] = "Origin", ["Publications"] = "Publications",
        ["Pending"] = "Pending", ["ExactCopies"] = "FilterExactCopies", ["OfCopyTypes"] = "OfCopyTypes",
        ["Signator"] = "Signators", ["Author"] = "Author", ["Creator"] = "Creator", ["AssignedTo"] = "AssignedTo",
        ["AnsweredBy"] = "AnsweredBy", ["ArchivedBy"] = "ArchivedBy",
        ["SentTo"] = "SentTo", ["SentFrom"] = "SentFrom", ["AnnouncedTo"] = "AnnouncedTo", ["AnnouncedFrom"] = "AnnouncedFrom",
        ["SignatureObligationBy"] = "SignatureObligationBy", ["CreatedForGroup"] = "CreatedForGroup",
    };

    private static List<int> Ints(JsonElement v) =>
        v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _)).Select(e => e.GetInt32()).ToList()
            : [];

    private static string? Str(JsonElement v, string prop) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>The issue-date range of a period (Common.Constants.DateRanges), inclusive; `to` null means today.</summary>
    internal static (DateTime? From, DateTime? To) PeriodRange(Period p, DateTime today) => p.Range switch
    {
        1 => (today, today),
        2 => (today.AddDays(-7), today),
        3 => (today.AddMonths(-1), today),
        4 => (today.AddYears(-1), today),
        5 => (new DateTime(today.Year, 1, 1), today),
        6 => (null, today),
        0 => (Json.ParseDay(p.From), Json.ParseDay(p.To)),
        _ => throw ApiException.BadRequest("Άγνωστη περίοδος."),
    };

    internal static DocumentRow MapRow(JsonNode n, int userId, IReadOnlyDictionary<int, StatusRow> statuses)
    {
        var copy = n["CurrentCopy"];
        var type = copy?["WorkflowDocumentTypeId"]?.GetValue<int?>() ?? n["Type"]?["Id"]?.GetValue<int?>() ?? 0;
        var statusId = copy?["WorkflowStatusId"]?.GetValue<int?>();
        var enableAlt = n["EnableAltProtocol"]?.GetValue<bool>() ?? false;
        var protocol = enableAlt ? n["ProtocolText"]?.GetValue<string>() : copy?["ProtocolText"]?.GetValue<string>();
        var protocolDate = enableAlt ? Date(n["ProtocolDate"]) : Date(copy?["ProtocolDate"]);
        var status = type == DocumentType.Draft && statusId == 3
            ? "Έχει αποσταλεί προς υπογραφή"
            : Text(n["CopyStatusCode"]) ?? (statusId is int s ? statuses.GetValueOrDefault(s)?.Title : null) ?? "";
        return new DocumentRow
        {
            Id = n["Id"]?.GetValue<int>() ?? n["IdDocument"]?.GetValue<int>() ?? 0,
            IdDocumentCopy = copy?["IdDocumentCopy"]?.GetValue<int>() ?? 0,
            Type = type,
            Title = Text(n["Title"]) ?? "(χωρίς τίτλο)",
            IssueDate = Json.Iso(Date(copy?["CreationDate"])) ?? "",
            LastSignatureDate = Json.Iso(Date(n["LastSignatureDate"])),
            ProtocolText = protocol,
            ProtocolDate = Json.Iso(protocolDate),
            SenderProtocol = Text(n["CopySenderProtocol"]) ?? Text(n["ArPrApostolea"]),
            Sender = Text(copy?["Sender"]),
            Recipients = Text(n["Recepients"]),
            SubKind = Text(n["EuriasXrisisKind"]?["Title"]),
            Status = status,
            IsFinal = statusId is int f && statuses.GetValueOrDefault(f)?.IsFinalStatus == true,
            SignaturesStatus = Text(n["SignaturesStatus"]),
            NextToSign = Text(n["NextEntityToSign"]),
            Kind = Text(n["Kind"]?["Title"]),
            Assignees = Text(copy?["AssignedToUsers"]),
            Folders = Text(copy?["DocumentFoldersCommaSep"]),
            Endiaferomenos = Text(n["Endiaferomenos"]),
            CreatedBy = Text(n["CreatedBy"]?["DisplayName"]) ?? Text(n["CreatedBy"]?["FullName"]),
            IsUnread = copy?["IsUnreadGroup"]?.GetValue<bool>() ?? false,
            IsReadByUser = Ids(n["ReadByUserIds"]).Contains(userId),
            IsImportant = Ids(n["ImportantToUserIds"]).Contains(userId),
            IsUrgent = copy?["IsUrgent"]?.GetValue<bool>() ?? false,
            HasComments = copy?["HasComments"]?.GetValue<bool>() ?? false,
            KeepUntil = Json.Iso(Date(n["DiatirisiEos"])),
        };
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static HashSet<int> Ids(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0).ToHashSet() : [];

    /// <summary>Dates of v1: ISO, or ServiceStack's "/Date(ms+zone)/".</summary>
    internal static DateTime? Date(JsonNode? n)
    {
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return null;
        if (s.StartsWith("/Date(", StringComparison.Ordinal))
        {
            var inner = s[6..s.IndexOf(')')];
            var sign = inner.IndexOfAny(['+', '-'], 1);
            var ms = long.Parse(sign > 0 ? inner[..sign] : inner);
            var utc = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            // ServiceStack writes local times with the server's offset; Docutracks stores local times.
            return sign > 0 ? utc.ToOffset(ParseOffset(inner[sign..])).DateTime : utc.LocalDateTime;
        }
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
    }

    private static TimeSpan ParseOffset(string z) =>
        new TimeSpan(int.Parse(z.Substring(1, 2)), int.Parse(z.Substring(3, 2)), 0) * (z[0] == '-' ? -1 : 1);

    private sealed class NextRow
    {
        public int IdDocument { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>
    /// «Κάτοχος» of drafts in one query (the Razor list calls GetDocumentById per row): the first
    /// pending signature, as the user or else the group.
    /// </summary>
    private async Task FillNextToSign(List<DocumentRow> rows, CancellationToken ct)
    {
        var drafts = rows.Where(r => r.Type == DocumentType.Draft && r.NextToSign is null).Select(r => r.Id).Distinct().ToList();
        if (drafts.Count == 0) return;
        var next = await db.Query<NextRow>(
            """
            SELECT s.IdDocument,
                   COALESCE(NULLIF(LTRIM(RTRIM(ISNULL(u.Surname, '') + ' ' + ISNULL(u.Name, ''))), ''), g.Name) AS Name
              FROM Document.Signature s
              LEFT JOIN Organization.OrgUser u ON u.IdOrgUser = s.IdOrgUser
              LEFT JOIN Organization.OrgGroup g ON g.IdOrgGroup = s.IdOrgGroup
             WHERE s.IdDocument IN @ids AND s.HasSigned = 0 AND ISNULL(s.IsSkipped, 0) = 0
               AND s.IdSignature = (SELECT MIN(s2.IdSignature) FROM Document.Signature s2
                                     WHERE s2.IdDocument = s.IdDocument AND s2.HasSigned = 0 AND ISNULL(s2.IsSkipped, 0) = 0)
            """, new { ids = drafts }, ct);
        var byDoc = next.ToDictionary(n => n.IdDocument, n => n.Name);
        foreach (var r in rows) if (r.Type == DocumentType.Draft && byDoc.TryGetValue(r.Id, out var name)) r.NextToSign = name;
    }

    /// <summary>Columns of a folder, as the `.Visible(...)` of List/Index.cshtml.</summary>
    private async Task<List<string>> ColumnsFor(FolderKey key, CancellationToken ct)
    {
        var all = (await filters.Columns(ct)).Select(c => c.Key);
        var recipientsForAll = await settings.Bool("ShowRecipientsForAllTypesAtDocumentIndex", false, ct);
        var t = key.DocType;
        return all.Where(k => k switch
        {
            "ProtocolNum" or "ProtocolDate" => t != DocumentType.Draft,
            "DocumentIdentifier" => t == DocumentType.Draft,
            "Recipients" => t == DocumentType.Outgoing || recipientsForAll,
            "NextEntityToSign" => t == DocumentType.Draft || t <= 0,
            "SignaturesStatus" => t == DocumentType.Draft && key.FolderType == 40,
            "ArPrApostolea" or "Apostoleas" => t != DocumentType.Outgoing && t != DocumentType.Draft,
            _ => true,
        }).ToList();
    }
}
