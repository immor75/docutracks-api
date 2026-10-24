using System.Globalization;
using System.Text.Json;

namespace Docutracks.Api.Documents;

/// <summary>
/// The fields a Razor form of Docutracks posts (names as in the HTML), built from the values of this
/// API. ClassicWriter posts them as the user, so DocumentModelBinder and the classic code decide
/// what they mean. Files: keys ending in `.Id.new` hold TempIds of `POST files`. New contacts: the value
/// `new:{n}` in a `*.Id` key, created by ClassicWriter from <see cref="NewContacts"/> before posting.
/// </summary>
public sealed class RazorFields
{
    public Dictionary<string, List<string>> Fields { get; } = new(StringComparer.Ordinal);
    public List<RazorNewContact> NewContacts { get; } = [];

    public RazorFields Add(string key, string? value)
    {
        if (value is null) return this;
        if (!Fields.TryGetValue(key, out var list)) Fields[key] = list = [];
        list.Add(value);
        return this;
    }

    public RazorFields Add(string key, int? value) => Add(key, value?.ToString(CultureInfo.InvariantCulture));

    public RazorFields Add(string key, bool value) => Add(key, value ? "true" : "false");

    public RazorFields AddAll(string key, IEnumerable<string> values)
    {
        foreach (var v in values) Add(key, v);
        return this;
    }

    /// <summary>A contact list (`Id` and `Email` aligned); a contact without Id is created before posting.</summary>
    public RazorFields Contacts(string prefix, IEnumerable<ContactValue> contacts)
    {
        foreach (var c in contacts)
        {
            if (c.Id is int id) Add(prefix + ".Id", id);
            else
            {
                if (string.IsNullOrWhiteSpace(c.Name)) continue;
                var reference = "new:" + NewContacts.Count;
                NewContacts.Add(new RazorNewContact(reference, c.Name.Trim(), string.IsNullOrWhiteSpace(c.Email) ? null : c.Email.Trim()));
                Add(prefix + ".Id", reference);
            }
            Add(prefix + ".Email", c.Email ?? "");
        }
        return this;
    }

    /// <summary>Files of a document: kept ones by Id, new ones by TempId.</summary>
    public RazorFields Files(string prefix, IEnumerable<UploadedFile> files)
    {
        foreach (var f in files)
        {
            if (f.FileId is int id) Add(prefix + ".Id", id);
            else if (!string.IsNullOrEmpty(f.TempId)) Add(prefix + ".Id.new", f.TempId);
        }
        return this;
    }

    /// <summary>Thematic folders (`Name`, `Id`; Id 0 = a new folder with that name).</summary>
    public RazorFields Folders(IEnumerable<ThematicFolder> folders)
    {
        foreach (var f in folders.Where(f => !string.IsNullOrWhiteSpace(f.Name)))
        {
            Add("CurrentDocument.CurrentCopy.DocumentFolders.Name", f.Name.Trim());
            Add("CurrentDocument.CurrentCopy.DocumentFolders.Id", f.Id ?? 0);
        }
        return this;
    }

    /// <summary>A user in a group as the organisation trees post it.</summary>
    public static string UserInGroup(PersonValue p) => p.UserId is int u ? $"{u}-{p.GroupId}" : p.GroupId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Dates as the el-GR forms post them.</summary>
    public static string? Day(string? iso) =>
        DateTime.TryParseExact(iso, ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "o"], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)
            ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : null;

    /* ---------- reading the JSON values of the client ---------- */

    public static bool Has(Dictionary<string, JsonElement> v, string key) =>
        v.TryGetValue(key, out var e) && e.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public static string? Text(Dictionary<string, JsonElement> v, string key) =>
        v.TryGetValue(key, out var e) ? e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    public static bool Bool(Dictionary<string, JsonElement> v, string key) =>
        v.TryGetValue(key, out var e) && (e.ValueKind == JsonValueKind.True || (e.ValueKind == JsonValueKind.String && bool.TryParse(e.GetString(), out var b) && b));

    public static int? Int(Dictionary<string, JsonElement> v, string key) =>
        v.TryGetValue(key, out var e) ? e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            JsonValueKind.Array => e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var i) ? (int?)i : null).FirstOrDefault(i => i is not null),
            _ => null,
        } : null;

    public static List<int> Ints(Dictionary<string, JsonElement> v, string key) =>
        v.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToList()
            : Int(v, key) is int one ? [one] : [];

    public static List<T> List<T>(Dictionary<string, JsonElement> v, string key)
    {
        if (!v.TryGetValue(key, out var e)) return [];
        try
        {
            return e.ValueKind switch
            {
                JsonValueKind.Array => e.Deserialize<List<T>>(Json.Options) ?? [],
                JsonValueKind.Object => e.Deserialize<T>(Json.Options) is { } one ? [one] : [],
                _ => [],
            };
        }
        catch (JsonException)
        {
            throw ApiException.BadRequest($"Λανθασμένη τιμή στο πεδίο {key}.");
        }
    }
}

public sealed record RazorNewContact(string Ref, string Name, string? Email);

/// <summary>Common.Constants used in the forms.</summary>
internal static class Classic
{
    public const int KinisiSend = 2, KinisiReturn = 3, KinisiInform = 6;
    public const int EnergeiaNone = 5;
    public const int RelationReply = 1, RelationOther = 6;
    public const int SignatureFinal = 1, SignatureEndorsement = 2, SignatureAuthor = 3, SignatureRatifier = 4;
    public const string Params = "ActionContext.Current.Parameters.";
}
