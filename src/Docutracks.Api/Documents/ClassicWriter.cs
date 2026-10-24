using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Docutracks.Api.Documents;

/// <summary>
/// All writes of the documents section, in one place: they post the forms of the classic UI
/// (<see cref="ClassicClient"/>) with the fields <see cref="RazorFields"/> builds. When the classic UI
/// goes away, typed services of v1 take the place of this class; the endpoints and the client stay.
/// </summary>
public sealed partial class ClassicWriter(ClassicClient classic, V1Client v1, TempFiles temp, IDb db, UserContext user, IOptions<ApiOptions> options)
{
    public sealed record Outcome(bool Success, int? DocumentId, string? Message);

    /* ---------- actions of the document view ---------- */

    /// <summary>
    /// A form of UiActions/UiViewDoc/{code}.cshtml: it is opened at `UiViewDoc/{code}/{id}` and posted to
    /// `UiViewDoc/{id}` with `uiactionexec = code` (Html.Form("UiViewDoc/" + id), UIActionExecButtonForEntities).
    /// </summary>
    public async Task<Outcome> Action(int groupId, int documentId, string code, RazorFields fields, CancellationToken ct)
    {
        await CreateContacts(groupId, fields, ct);
        var files = Files(fields);
        fields.Add("uiactionexec", code).Add("uiactionsuccess", "UiViewDoc");
        var r = await classic.Post(
            classic.Url("Document", groupId, $"UiViewDoc/{documentId}"), fields.Fields, files,
            classic.Url("Document", groupId, $"UiViewDoc/{code}/{documentId}"), ct);
        return new Outcome(r.Success, documentId, r.Success ? r.Message : r.Message ?? "Η ενέργεια δεν ολοκληρώθηκε.");
    }

    /* ---------- new document, edit ---------- */

    /// <summary>
    /// UiNew.cshtml (`UiNew/UiNew`) or UiEdit.cshtml (`UiEdit/{id}`), with the fields of
    /// DocumentPropertiesEditor. A draft also posts its signature chain, as the «signatureDiagram»
    /// of the form: what `_Helpers/EndorsementCalculation` returns for these fields.
    /// </summary>
    public async Task<Outcome> Register(int groupId, int? documentId, RazorFields fields, bool draft, bool resetSignatures, CancellationToken ct)
    {
        await CreateContacts(groupId, fields, ct);
        var files = Files(fields);
        var page = documentId is int id ? classic.Url("Document", groupId, $"UiEdit/{id}") : classic.Url("Document", groupId, "UiNew/UiNew");
        if (draft)
            foreach (var (key, value) in await Chain(groupId, documentId, fields.Fields, resetSignatures || documentId is null, page, ct))
                fields.Add(key, value);
        var code = documentId is null ? "UiNew" : "UiEdit";
        fields.Add("uiactionexec", code).Add("uiactionsuccess", "UiViewDoc");
        var path = documentId is int edit ? $"UiEdit/{edit}" : "UiNew/UiNew";
        var r = await classic.Post(classic.Url("Document", groupId, path), fields.Fields, files, page, ct);
        if (r.Success) temp.Delete(files.Select(f => Path.GetFileName(f.Path)));
        return new Outcome(r.Success, r.Success ? r.EntityId ?? documentId : null, r.Success ? r.Message : r.Message ?? "Η καταχώριση δεν ολοκληρώθηκε.");
    }

    private static readonly string[] ChainKeys =
    [
        "CurrentDocument.CoAuthorsWithSignature.Signator.Id",
        "CurrentDocument.Cosignatures.ToSign.Id",
        "CurrentDocument.RatifierSignatures.ToSign.Id",
        "CurrentDocument.Signatures.ToSign.Id",
    ];

    /// <summary>
    /// The hidden inputs of EndorsementCalculation.cshtml ("{id}&amp;Calculated", in the order of the
    /// diagram): the call the editor makes on «Υπολογισμός υπογραφών» (`?reset=true`), or with the
    /// stored chain of a saved draft.
    /// </summary>
    public async Task<List<(string Key, string Value)>> Chain(int groupId, int? documentId, IReadOnlyDictionary<string, List<string>> fields,
        bool reset, string? tokenPage, CancellationToken ct)
    {
        var path = classic.Url("Document", groupId, "_Helpers/EndorsementCalculation" + (documentId is int id ? $"/{id}" : "")) + (reset ? "?reset=true" : "");
        var html = await classic.PostRead(path, fields, tokenPage ?? classic.Url("Document", groupId, "UiNew/UiNew"), ct);
        return ChainInputs(html);
    }

    internal static List<(string Key, string Value)> ChainInputs(string html)
    {
        var list = new List<(string, string)>();
        foreach (Match m in HiddenInput().Matches(html))
        {
            var tag = m.Value;
            var name = Attr(tag, "name");
            var value = Attr(tag, "value");
            if (name is null || value is null || !ChainKeys.Contains(name)) continue;
            value = System.Net.WebUtility.HtmlDecode(value);
            // The picks of the user (plain ids) are already among the fields; only the diagram is added.
            if (!value.EndsWith("&Calculated", StringComparison.Ordinal)) continue;
            list.Add((name, value));
        }
        return list;
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{name}=(['""])(.*?)\1", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[2].Value : null;
    }

    /* ---------- flags, export, OTP ---------- */

    /// <summary>DocumentController.UpdateDocumentReadyStatus / UpdateDocumentImportance (JSON `{ Result, Message }`).</summary>
    public async Task<List<int>> Flags(int groupId, IEnumerable<int> documentIds, bool? isRead, bool? isImportant, CancellationToken ct)
    {
        var updated = new List<int>();
        foreach (var id in documentIds)
        {
            var ok = true;
            if (isRead is bool read)
                ok &= Ok(await classic.PostJson(classic.Url("Document", groupId, "UpdateDocumentReadyStatus"),
                    Form(("documentId", id.ToString(CultureInfo.InvariantCulture)), ("setAsRead", read ? "true" : "false")), ct));
            if (isImportant is bool important)
                ok &= Ok(await classic.PostJson(classic.Url("Document", groupId, "UpdateDocumentImportance"),
                    Form(("documentId", id.ToString(CultureInfo.InvariantCulture)), ("setAsImportant", important ? "true" : "false")), ct));
            if (ok) updated.Add(id);
        }
        return updated;

        static bool Ok(JsonNode? n) => string.Equals(n?["Result"]?.GetValue<string>(), "OK", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ListController.RequestDocumentExport / ExportDocumentsExtended with `docIds` (the Gateway sends the Excel by e-mail).</summary>
    public async Task Export(int groupId, IReadOnlyList<int> documentIds, bool withFiles, string tokenPage, CancellationToken ct, params string[] fallbackPages)
    {
        var action = withFiles ? "ExportDocumentsExtended" : "RequestDocumentExport";
        var text = await classic.PostRead(classic.Url("List", groupId, action),
            Form(("docIds", string.Join(",", documentIds))), tokenPage, ct, fallbackPages);
        JsonNode? n = null;
        try { n = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
        if (n?["success"]?.GetValue<bool>() != true)
            throw new ApiException(n?["error"]?.GetValue<string>() is { Length: > 0 } e ? $"Η εξαγωγή δεν ξεκίνησε: {e}" : "Η εξαγωγή δεν ξεκίνησε.");
    }

    /// <summary>HelperController.RequestOtpForSDDDSign: an OTP of ΣΗΔΕ to the user's phone.</summary>
    public async Task<string> Otp(int groupId, string username, string password, CancellationToken ct)
    {
        var n = await classic.PostJson($"{options.Value.Classic.Culture.Trim('/')}/Helper/RequestOtpForSDDDSign",
            Form(("username", username), ("password", password)), ct);
        if (n?["Success"]?.GetValue<bool>() != true)
            throw new ApiException(n?["ErrorMessage"]?.GetValue<string>() is { Length: > 0 } e ? $"Το OTP δεν στάλθηκε: {e}" : "Το OTP δεν στάλθηκε. Ελέγξτε όνομα χρήστη και κωδικό.");
        return n?["Description"]?.GetValue<string>() is { Length: > 0 } d ? d : "Το OTP στάλθηκε στο κινητό σας.";
    }

    private static Dictionary<string, List<string>> Form(params (string Key, string Value)[] pairs) =>
        pairs.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Select(p => p.Value).ToList());

    /* ---------- contacts and files ---------- */

    /// <summary>
    /// New contacts by name, as the editor does for a name typed in the autocomplete
    /// (v1 `contacts/getcontactid` with AddSender: an existing contact of that name, or a new one).
    /// </summary>
    private async Task CreateContacts(int groupId, RazorFields fields, CancellationToken ct)
    {
        if (fields.NewContacts.Count == 0) return;
        var type = options.Value.Classic.NewContactTypeId;
        if (type <= 0)
            type = await db.Scalar<int?>("SELECT TOP 1 IdContactType FROM Contact.Contact WHERE IdContactType IS NOT NULL GROUP BY IdContactType ORDER BY COUNT(1) DESC", ct: ct) ?? 1;
        var ids = new Dictionary<string, string>();
        foreach (var c in fields.NewContacts)
        {
            var n = await v1.Json(HttpMethod.Post, "services/contacts/getcontactid", new { c.Name, AddSender = true, ContactTypeId = type }, ct);
            var id = n?["ContactId"]?.GetValue<long?>() ?? 0;
            // Several contacts with this name: getcontactid answers 0; take the oldest.
            if (id <= 0) id = await db.Scalar<long?>("SELECT MIN(IdContact) FROM Contact.Contact WHERE Eponimia = @name", new { name = c.Name }, ct) ?? 0;
            if (id <= 0) throw new ApiException($"Η επαφή «{c.Name}» δεν δημιουργήθηκε.");
            ids[c.Ref] = id.ToString(CultureInfo.InvariantCulture);
        }
        foreach (var list in fields.Fields.Values)
            for (var i = 0; i < list.Count; i++)
                if (ids.TryGetValue(list[i], out var real)) list[i] = real;
    }

    /// <summary>The `*.Id.new` fields become the files of the form (uploads of this user only).</summary>
    private List<ClassicClient.FormFile> Files(RazorFields fields)
    {
        var me = user.Required.Id;
        var files = new List<ClassicClient.FormFile>();
        foreach (var key in fields.Fields.Keys.Where(k => k.EndsWith(".Id.new", StringComparison.Ordinal)).ToList())
        {
            foreach (var tempId in fields.Fields[key])
            {
                var meta = temp.Find(tempId, me) ?? throw new ApiException("Ένα αρχείο δεν βρέθηκε· ανεβάστε το ξανά.");
                files.Add(new ClassicClient.FormFile(key, meta.Name, temp.PathOf(tempId)));
            }
            fields.Fields.Remove(key);
        }
        return files;
    }

    [GeneratedRegex(@"<input\b[^>]*type=['""]hidden['""][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInput();
}
