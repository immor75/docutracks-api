using System.Text.Json.Nodes;

namespace Docutracks.Api.Documents;

/// <summary>
/// The values of an existing document in the keys of the registration form: for «Επεξεργασία»
/// (current values), «Απάντηση» and «Κλωνοποίηση» (prefill).
/// </summary>
public sealed class DocumentValues(IDb db, ViewService view)
{
    private sealed class DocRow
    {
        public int IdDocument { get; set; }
        public int IdDocumentType { get; set; }
        public string? Title { get; set; }
        public string? Summary { get; set; }
        public int? IdDocumentKind { get; set; }
        public string? DocumentKindMetadataInstance { get; set; }
        public string? ArPrApostolea { get; set; }
        public DateTime? DateApostolis { get; set; }
        public string? Comments { get; set; }
        public string? Endiaferomenos { get; set; }
        public int? IdPriority { get; set; }
        public int? IdDiavathmisi { get; set; }
        public bool? IsPersonal { get; set; }
        public bool? ContainsSensitiveData { get; set; }
        public DateTime? DiatirisiEos { get; set; }
        public int? RetentionInMonths { get; set; }
        public bool? IsOikothen { get; set; }
        public string? ToposEkdosis { get; set; }
        public int? IdAuthor { get; set; }
        public int? IdCreatedForGroup { get; set; }
        public string? EksoterikoiYpografontes { get; set; }
        public bool? EnableAltProtocol { get; set; }
        public int? IdAltProtocol { get; set; }
        public string? ProtocolText { get; set; }
        public int? AltNumberKind { get; set; }
    }

    private sealed class PersonRow
    {
        public int? IdOrgUser { get; set; }
        public int? IdOrgGroup { get; set; }
        public int Kind { get; set; }
    }

    private sealed class ContactRow
    {
        public int IdContact { get; set; }
        public string? Name { get; set; }
        public int IdContactRelation { get; set; }
    }

    private sealed class IdRow
    {
        public int Id { get; set; }
        public string? Title { get; set; }
    }

    public sealed record Result(int DocumentType, string Title, string? ProtocolText, bool HasProtocol, Dictionary<string, object?> Values);

    public async Task<Result> Of(int id, int groupId, CancellationToken ct)
    {
        var (_, copy) = await view.Accessible(id, groupId, ct);
        var d = await db.First<DocRow>(
            """
            SELECT d.IdDocument, d.IdDocumentType, d.Title, d.Summary, d.IdDocumentKind, d.DocumentKindMetadataInstance, d.ArPrApostolea, d.DateApostolis,
                   d.Comments, d.Endiaferomenos, d.IdPriority, d.IdDiavathmisi, d.IsPersonal, d.ContainsSensitiveData, d.DiatirisiEos, d.RetentionInMonths,
                   d.IsOikothen, d.ToposEkdosis, d.IdAuthor, d.IdCreatedForGroup, d.EksoterikoiYpografontes, d.EnableAltProtocol, d.IdAltProtocol,
                   (SELECT TOP 1 p.ProtocolText FROM Document.Protokollo p WHERE p.IdDocument = d.IdDocument ORDER BY p.IdProtokollo) AS ProtocolText,
                   (SELECT TOP 1 an.IdAltNumberKind FROM Document.AltNumbering an WHERE an.IdDocument = d.IdDocument) AS AltNumberKind
              FROM Document.Document d WHERE d.IdDocument = @id
            """, new { id }, ct) ?? throw ApiException.NotFound();

        var v = new Dictionary<string, object?>
        {
            ["Title"] = d.Title,
            ["Summary"] = d.Summary,
            ["KindId"] = d.IdDocumentKind,
            ["KindData"] = KindData(d.DocumentKindMetadataInstance),
            ["SenderProtocol"] = d.ArPrApostolea ?? copy.SenderProtocol,
            ["SendDate"] = Json.Day(d.DateApostolis),
            ["Comments"] = d.Comments,
            ["Endiaferomenos"] = d.Endiaferomenos,
            ["Priority"] = d.IdPriority,
            ["Classification"] = d.IdDiavathmisi,
            ["IsPersonal"] = d.IsPersonal == true,
            ["SensitiveData"] = d.ContainsSensitiveData == true,
            ["KeepUntil"] = Json.Day(d.DiatirisiEos),
            ["RetentionInMonths"] = d.RetentionInMonths,
            ["Oikothen"] = d.IsOikothen == true,
            ["IssuePlace"] = d.ToposEkdosis,
            ["ExternalSigners"] = d.EksoterikoiYpografontes,
            ["ProtocolBook"] = d.EnableAltProtocol == true ? d.IdAltProtocol ?? 0 : 0,
            ["AltNumbering"] = d.AltNumberKind,
            ["Author"] = d.IdAuthor is int a ? new List<PersonValue> { new() { UserId = a, GroupId = groupId } } : new List<PersonValue>(),
            ["CreatedForGroup"] = d.IdCreatedForGroup is int g ? new List<PersonValue> { new() { GroupId = g } } : new List<PersonValue>(),
        };

        v["Categories"] = (await db.Query<IdRow>("SELECT IdCategory AS Id FROM [Document].[DocumentCategory] WHERE IdDocument = @id", new { id }, ct)).Select(r => r.Id).ToList();
        v["SendTypes"] = (await db.Query<IdRow>("SELECT IdSendType AS Id FROM [Document].[DocumentSendType] WHERE IdDocument = @id", new { id }, ct)).Select(r => r.Id).ToList();
        v["ThematicFolders"] = (await db.Query<IdRow>(
                "SELECT f.IdFolder AS Id, f.Name AS Title FROM Document.DocumentCopyFolder dcf INNER JOIN Document.Folder f ON f.IdFolder = dcf.IdFolder WHERE dcf.IdDocumentCopy = @copyId",
                new { copyId = copy.IdDocumentCopy }, ct))
            .Select(r => new ThematicFolder { Id = r.Id, Name = r.Title ?? "" }).ToList();
        v["Related"] = (await db.Query<IdRow>(
                "SELECT dd.IdDocumentSxetiko AS Id, d.Title FROM Document.DocumentDocument dd INNER JOIN Document.Document d ON d.IdDocument = dd.IdDocumentSxetiko WHERE dd.IdDocument = @id AND d.IsDeleted = 0",
                new { id }, ct))
            .Select(r => new { Id = r.Id, Title = r.Title ?? $"Α/Α {r.Id}" }).ToList();

        // Contacts by relation: 3 sender, 1 recipients, 2 notified, 14/15 transactors, 16 on behalf of.
        var contacts = await db.Query<ContactRow>(
            """
            SELECT c.IdContact, dc.IdContactRelation,
                   COALESCE(NULLIF(c.Eponimia, ''), LTRIM(RTRIM(ISNULL(c.LastName, '') + ' ' + ISNULL(c.FirstName, '')))) AS Name
              FROM Document.DocumentContact dc INNER JOIN Contact.Contact c ON c.IdContact = dc.IdContact
             WHERE dc.IdDocument = @id ORDER BY dc.IdDocumentContact
            """, new { id }, ct);
        List<ContactValue> C(params int[] rel) => contacts.Where(c => rel.Contains(c.IdContactRelation)).Select(c => new ContactValue { Id = c.IdContact, Name = c.Name ?? "" }).ToList();
        v["Sender"] = C(3);
        v["OtherRecipients"] = C(1);
        v["OtherNotify"] = C(2);
        v["Transactors"] = C(14, 15);
        v["OnBehalfOf"] = C(16);

        // Internal recipients by distribution: 1 recipients, 2 notified, 3 internal distribution.
        var internalRows = await db.Query<PersonRow>(
            "SELECT IdOrgUser, IdOrgGroup, ISNULL(IdDistributionType, 1) AS Kind FROM Document.Recipient WHERE IdDocument = @id ORDER BY IdRecipient",
            new { id }, ct);
        List<PersonValue> P(int kind) => internalRows.Where(r => r.Kind == kind && r.IdOrgGroup is not null)
            .Select(r => new PersonValue { UserId = r.IdOrgUser, GroupId = r.IdOrgGroup!.Value }).ToList();
        v["Recipients"] = P(1);
        v["Notify"] = P(2);
        v["InternalDistribution"] = P(3);

        // Files as kept files (FileId), by role.
        var files = await view.Files(id, false, ct);
        UploadedFile U(DocumentFile f) => new() { FileId = f.Id, Name = f.Name, Size = f.Size ?? 0, IsPdf = f.IsPdf };
        v["MainFile"] = files.Where(f => f.Role == "main").Select(U).ToList();
        v["Attachments"] = files.Where(f => f.Role == "attachment").Select(U).ToList();
        v["InternalAttachments"] = files.Where(f => f.Role == "internal").Select(U).ToList();
        v["SignerAttachments"] = files.Where(f => f.Role == "signer").Select(U).ToList();

        return new Result(d.IdDocumentType, d.Title ?? $"Α/Α {id}", d.ProtocolText, d.ProtocolText is not null || copy.ProtocolText is not null, v);
    }

    /// <summary>The «Ειδικά πεδία» of the kind: the `data` of the Formio submission stored on the document.</summary>
    private static object? KindData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var n = JsonNode.Parse(json);
            return n?["data"] is JsonObject data ? new JsonNodeValue(data) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
