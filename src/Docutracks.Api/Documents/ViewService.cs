using System.IO.Compression;

namespace Docutracks.Api.Documents;

/// <summary>
/// `GET {id}`, `GET {id}/history`, `GET {id}/files`: what UIViewDoc shows, read from SQL. Access as
/// v1: a copy owned or created by the group; otherwise v1 decides (View().CanExecute, e.g. drafts to sign).
/// </summary>
public sealed class ViewService(IDb db, IReference reference, V1Mobile mobile, V1Client v1, UserContext user)
{
    private sealed class DocRow
    {
        public int IdDocument { get; set; }
        public string? Title { get; set; }
        public int IdDocumentType { get; set; }
        public string? Kind { get; set; }
        public string? SubKind { get; set; }
        public string? ArPrApostolea { get; set; }
        public DateTime? DateApostolis { get; set; }
        public string? Priority { get; set; }
        public string? Classification { get; set; }
        public bool? ContainsSensitiveData { get; set; }
        public bool? IsPersonal { get; set; }
        public string? Comments { get; set; }
        public string? CreatedBy { get; set; }
        public string? CreatedForGroup { get; set; }
        public string? Author { get; set; }
        public string? Endiaferomenos { get; set; }
        public DateTime? DiatirisiEos { get; set; }
        public bool? EnableAltProtocol { get; set; }
        public string? AltProtocolText { get; set; }
        public DateTime? DateAltProtocolBook { get; set; }
        public string? AltProtocolName { get; set; }
        public bool? IsVerifiedWithDigitalSignature { get; set; }
        public string? DecisionNumber { get; set; }
        public string? Ada { get; set; }
        public bool IsImportant { get; set; }
    }

    internal sealed class CopyRow
    {
        public int IdDocumentCopy { get; set; }
        public DateTime? CreationDate { get; set; }
        public int? Status { get; set; }
        public string? Sender { get; set; }
        public string? SenderProtocol { get; set; }
        public bool IsUrgent { get; set; }
        public int? IdOwnedByGroup { get; set; }
        public int? IdCreatedByGroup { get; set; }
        public string? CreatedByGroup { get; set; }
        public string? OwnerGroup { get; set; }
        public string? AssignedTo { get; set; }
        public string? ProtocolText { get; set; }
        public DateTime? ProtocolDate { get; set; }
    }

    /// <summary>The copies of a document; the one of the group first (owned, then created), as DocumentListForMobileDto picks it.</summary>
    internal async Task<(List<CopyRow> All, CopyRow? Mine)> Copies(int documentId, int groupId, CancellationToken ct)
    {
        var all = await db.Query<CopyRow>(
            """
            SELECT dc.IdDocumentCopy, dc.CreationDate, dc.Status, dc.Sender, dc.SenderProtocol, dc.IsUrgent, dc.IdOwnedByGroup, dc.IdCreatedByGroup,
                   cg.Name AS CreatedByGroup, og.Name AS OwnerGroup,
                   NULLIF(LTRIM(RTRIM(ISNULL(au.Surname, '') + ' ' + ISNULL(au.Name, ''))), '') AS AssignedTo,
                   p.ProtocolText, p.DateProtokollou AS ProtocolDate
              FROM Document.DocumentCopy dc
              LEFT JOIN Organization.OrgGroup cg ON cg.IdOrgGroup = dc.IdCreatedByGroup
              LEFT JOIN Organization.OrgGroup og ON og.IdOrgGroup = dc.IdOwnedByGroup
              LEFT JOIN Organization.OrgUser au ON au.IdOrgUser = dc.IdAssignedToUser
              LEFT JOIN Document.Protokollo p ON p.IdProtokollo = dc.IdProtokollo
             WHERE dc.IdDocument = @documentId
             ORDER BY dc.CreationDate, dc.IdDocumentCopy
            """, new { documentId }, ct);
        var mine = all.FirstOrDefault(c => c.IdOwnedByGroup == groupId) ?? all.FirstOrDefault(c => c.IdCreatedByGroup == groupId);
        return (all, mine);
    }

    /// <summary>Refuses documents the user may not see in this group.</summary>
    internal async Task<(List<CopyRow> All, CopyRow Current)> Accessible(int documentId, int groupId, CancellationToken ct)
    {
        var deleted = await db.Scalar<bool?>("SELECT IsDeleted FROM Document.Document WHERE IdDocument = @documentId", new { documentId }, ct);
        if (deleted is null or true) throw ApiException.NotFound();
        var (all, mine) = await Copies(documentId, groupId, ct);
        if (mine is not null) return (all, mine);
        // No copy in the group (a draft to sign, a notified document, …): v1 decides with View().CanExecute.
        if (all.Count == 0 || !await mobile.CanSee(documentId, groupId, ct)) throw ApiException.NotFound();
        return (all, all[0]);
    }

    public async Task<DocumentView> View(int id, int groupId, CancellationToken ct)
    {
        var (copies, copy) = await Accessible(id, groupId, ct);
        var userId = user.Required.Id;
        var d = await db.First<DocRow>(
            $"""
            SELECT d.IdDocument, d.Title, d.IdDocumentType, d.ArPrApostolea, d.DateApostolis, d.ContainsSensitiveData, d.IsPersonal, d.Comments,
                   d.Endiaferomenos, d.DiatirisiEos, d.EnableAltProtocol, d.IsVerifiedWithDigitalSignature,
                   {Sql.Title("[Document].[DocumentKindTitleResources]", "d.IdDocumentKind")} AS Kind,
                   (SELECT TOP 1 {Sql.Title("[Document].[EureiasXrisisKindTitleResources]", "ex.IdEureiasXrisisKind")} FROM Document.EureiaXrisi ex WHERE ex.IdDocument = d.IdDocument) AS SubKind,
                   {Sql.Title("[Document].[PriorityTitleResources]", "d.IdPriority")} AS Priority,
                   {Sql.Title("[Document].[DiavathmisiTitleResources]", "d.IdDiavathmisi")} AS Classification,
                   NULLIF(LTRIM(RTRIM(ISNULL(cu.Surname, '') + ' ' + ISNULL(cu.Name, ''))), '') AS CreatedBy,
                   NULLIF(LTRIM(RTRIM(ISNULL(au.Surname, '') + ' ' + ISNULL(au.Name, ''))), '') AS Author,
                   fg.Name AS CreatedForGroup,
                   ab.AltProtocolText, ab.DateAltProtocolBook, ap.AltProtocolName,
                   dec.DecisionNumber, dec.Ada,
                   CASE WHEN EXISTS (SELECT 1 FROM Document.DocumentImportantToUser i WHERE i.IdDocument = d.IdDocument AND i.IdOrgUser = @userId) THEN 1 ELSE 0 END AS IsImportant
              FROM Document.Document d
              LEFT JOIN Organization.OrgUser cu ON cu.IdOrgUser = d.IdCreatedByUser
              LEFT JOIN Organization.OrgUser au ON au.IdOrgUser = d.IdAuthor
              LEFT JOIN Organization.OrgGroup fg ON fg.IdOrgGroup = d.IdCreatedForGroup
              LEFT JOIN Document.AltProtocolBook ab ON ab.IdDocument = d.IdDocument
              LEFT JOIN Document.AltProtocol ap ON ap.IdAltProtocol = ab.IdAltProtocol
              OUTER APPLY (SELECT TOP 1 x.DecisionNumber, x.Ada FROM Document.Decision x WHERE x.IdDocument = d.IdDocument ORDER BY x.IdDecision DESC) dec
             WHERE d.IdDocument = @id
            """, new { id, userId }, ct) ?? throw ApiException.NotFound();

        var statuses = await reference.Statuses(ct);
        var alt = d.EnableAltProtocol == true;
        var protocolText = alt ? d.AltProtocolText : copy.ProtocolText;
        var draftSent = d.IdDocumentType == DocumentType.Draft && copy.Status == 3;

        return new DocumentView
        {
            Id = d.IdDocument,
            IdDocumentCopy = copy.IdDocumentCopy,
            Type = d.IdDocumentType,
            Title = string.IsNullOrWhiteSpace(d.Title) ? "(χωρίς τίτλο)" : d.Title,
            Kind = d.Kind,
            SubKind = d.SubKind,
            Status = draftSent ? "Έχει αποσταλεί προς υπογραφή" : StatusTitle(statuses, copy.Status),
            IsFinal = copy.Status is int s && statuses.GetValueOrDefault(s)?.IsFinalStatus == true,
            IssueDate = Json.Iso(copy.CreationDate) ?? "",
            ProtocolText = protocolText,
            ProtocolDate = Json.Iso(alt ? d.DateAltProtocolBook : copy.ProtocolDate),
            ProtocolBook = alt ? d.AltProtocolName : null,
            SenderProtocol = copy.SenderProtocol ?? d.ArPrApostolea,
            Sender = copy.Sender ?? await Sender(id, ct),
            SendDate = Json.Iso(d.DateApostolis),
            Recipients = await Recipients(id, ct),
            Priority = d.Priority,
            Classification = d.Classification,
            ContainsPersonalData = d.IsPersonal == true || d.ContainsSensitiveData == true ? true : null,
            DecisionNumber = d.DecisionNumber,
            Ada = d.Ada,
            Comments = string.IsNullOrWhiteSpace(d.Comments) ? null : d.Comments,
            CreatedBy = d.CreatedBy,
            CreatedForGroup = d.CreatedForGroup,
            Author = d.Author,
            Endiaferomenos = d.Endiaferomenos,
            KeepUntil = Json.Iso(d.DiatirisiEos),
            Categories = await Categories(id, ct),
            ThematicFolders = await Thematic(copy.IdDocumentCopy, ct),
            Files = await Files(id, d.IsVerifiedWithDigitalSignature == true, ct),
            Signatures = await Signatures(id, ct),
            Copies = copies.Select(c => new DocumentCopyRow
            {
                Id = c.IdDocumentCopy,
                CreatedAt = Json.Iso(c.CreationDate) ?? "",
                CreatedByGroup = c.CreatedByGroup ?? "",
                OwnerGroup = c.OwnerGroup ?? c.CreatedByGroup ?? "",
                AssignedTo = c.AssignedTo,
                Status = StatusTitle(statuses, c.Status),
                ProtocolText = c.ProtocolText,
            }).ToList(),
            Related = await Related(id, ct),
            Folders = [],
            IsImportant = d.IsImportant,
            IsUrgent = copy.IsUrgent,
        };
    }

    private static string StatusTitle(IReadOnlyDictionary<int, StatusRow> statuses, int? id) =>
        id is int s && statuses.TryGetValue(s, out var row) ? row.Title ?? row.StatusCode ?? "" : "";

    private sealed class NameRow
    {
        public string? Name { get; set; }
        public bool Flag { get; set; }
    }

    /// <summary>The contact with relation PROTOCOLSENDER (3).</summary>
    private Task<string?> Sender(int id, CancellationToken ct) => db.Scalar<string>(
        """
        SELECT TOP 1 COALESCE(NULLIF(c.Eponimia, ''), LTRIM(RTRIM(ISNULL(c.LastName, '') + ' ' + ISNULL(c.FirstName, ''))))
          FROM Document.DocumentContact dc INNER JOIN Contact.Contact c ON c.IdContact = dc.IdContact
         WHERE dc.IdDocument = @id AND dc.IdContactRelation = 3
        """, new { id }, ct);

    /// <summary>Internal recipients (Document.Recipient: groups, Κοινοποίηση = distribution 2) and external ones (contacts 1 and 2).</summary>
    private async Task<List<DocumentRecipient>> Recipients(int id, CancellationToken ct)
    {
        var internalRows = await db.Query<NameRow>(
            """
            SELECT COALESCE(g.Name, NULLIF(LTRIM(RTRIM(ISNULL(u.Surname, '') + ' ' + ISNULL(u.Name, ''))), '')) AS Name,
                   CASE WHEN r.IdDistributionType = 2 THEN 1 ELSE 0 END AS Flag
              FROM Document.Recipient r
              LEFT JOIN Organization.OrgGroup g ON g.IdOrgGroup = r.IdOrgGroup
              LEFT JOIN Organization.OrgUser u ON u.IdOrgUser = r.IdOrgUser
             WHERE r.IdDocument = @id AND ISNULL(r.IdDistributionType, 1) IN (1, 2)
             ORDER BY r.IdRecipient
            """, new { id }, ct);
        var externalRows = await db.Query<NameRow>(
            """
            SELECT COALESCE(NULLIF(c.Eponimia, ''), LTRIM(RTRIM(ISNULL(c.LastName, '') + ' ' + ISNULL(c.FirstName, '')))) AS Name,
                   CASE WHEN dc.IdContactRelation = 2 THEN 1 ELSE 0 END AS Flag
              FROM Document.DocumentContact dc INNER JOIN Contact.Contact c ON c.IdContact = dc.IdContact
             WHERE dc.IdDocument = @id AND dc.IdContactRelation IN (1, 2)
             ORDER BY dc.IdDocumentContact
            """, new { id }, ct);
        return internalRows.Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new DocumentRecipient { Name = r.Name!, Internal = true, Cc = r.Flag ? true : null })
            .Concat(externalRows.Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => new DocumentRecipient { Name = r.Name!, Internal = false, Cc = r.Flag ? true : null }))
            .ToList();
    }

    private sealed class TextRow
    {
        public string? Text { get; set; }
    }

    private async Task<List<string>> Categories(int id, CancellationToken ct) =>
        (await db.Query<TextRow>(
            $"SELECT {Sql.Title("[Document].[CategoryTitleResources]", "dc.IdCategory")} AS Text FROM [Document].[DocumentCategory] dc WHERE dc.IdDocument = @id",
            new { id }, ct)).Select(r => r.Text).OfType<string>().ToList();

    private async Task<List<string>> Thematic(int copyId, CancellationToken ct) =>
        (await db.Query<TextRow>(
            "SELECT f.Name AS Text FROM Document.DocumentCopyFolder dcf INNER JOIN Document.Folder f ON f.IdFolder = dcf.IdFolder WHERE dcf.IdDocumentCopy = @copyId ORDER BY f.Name",
            new { copyId }, ct)).Select(r => r.Text).OfType<string>().ToList();

    internal sealed class FileRow
    {
        public int IdSingleFile { get; set; }
        public string? FileName { get; set; }
        public string? Extention { get; set; }
        public long? ByteSize { get; set; }
        public bool? IsMain { get; set; }
        public bool? IsGenerated { get; set; }
        public bool? IsInternal { get; set; }
        public bool? IsSigner { get; set; }
        public bool? IsLatestMain { get; set; }
    }

    /// <summary>The latest main file and the attachments (UIViewDoc); ids for `/services/file/get/{id}`.</summary>
    internal async Task<List<DocumentFile>> Files(int id, bool signed, CancellationToken ct)
    {
        var rows = await db.Query<FileRow>(
            """
            SELECT sf.IdSingleFile, sf.FileName, sf.Extention, sf.ByteSize, df.IsMain, df.IsGenerated, df.IsInternal, df.IsSigner, df.IsLatestMain
              FROM Document.DocumentFile df INNER JOIN Document.SingleFile sf ON sf.IdSingleFile = df.IdSingleFile
             WHERE df.IdDocument = @id
             ORDER BY df.IdDocumentFile
            """, new { id }, ct);
        return rows
            .Where(r => !(r.IsMain == true || r.IsGenerated == true) || r.IsLatestMain == true)
            .Select(r =>
            {
                var main = r.IsMain == true || r.IsGenerated == true;
                var name = r.FileName ?? $"Αρχείο {r.IdSingleFile}";
                var ext = (r.Extention ?? Path.GetExtension(name)).TrimStart('.').ToLowerInvariant();
                return new DocumentFile
                {
                    Id = r.IdSingleFile,
                    Name = name,
                    Size = r.ByteSize,
                    Role = main ? "main" : r.IsInternal == true ? "internal" : r.IsSigner == true ? "signer" : "attachment",
                    IsPdf = ext == "pdf",
                    IsSigned = main && signed ? true : null,
                };
            })
            .OrderBy(f => f.Role switch { "main" => 0, "attachment" => 1, "internal" => 2, _ => 3 })
            .ToList();
    }

    private sealed class SignatureRow
    {
        public string? UserName { get; set; }
        public string? GroupName { get; set; }
        public bool HasSigned { get; set; }
        public DateTime? SignatureDate { get; set; }
        public string? Role { get; set; }
    }

    private async Task<List<DocumentSignature>> Signatures(int id, CancellationToken ct) =>
        (await db.Query<SignatureRow>(
            $"""
            SELECT NULLIF(LTRIM(RTRIM(ISNULL(u.Surname, '') + ' ' + ISNULL(u.Name, ''))), '') AS UserName, g.Name AS GroupName,
                   s.HasSigned, s.SignatureDate, {Sql.Title("[Document].[SignatureTypeTitleResources]", "s.IdSignatureType")} AS Role
              FROM Document.Signature s
              LEFT JOIN Organization.OrgUser u ON u.IdOrgUser = s.IdOrgUser
              LEFT JOIN Organization.OrgGroup g ON g.IdOrgGroup = s.IdOrgGroup
             WHERE s.IdDocument = @id AND ISNULL(s.IsSkipped, 0) = 0
             ORDER BY s.IdSignature
            """, new { id }, ct))
        .Select(s => new DocumentSignature
        {
            Name = s.UserName ?? s.GroupName ?? "",
            Group = s.UserName is null ? null : s.GroupName,
            SignedAt = s.HasSigned ? Json.Iso(s.SignatureDate) ?? "" : null,
            Role = s.Role,
        }).ToList();

    private sealed class RelatedRow
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public int Type { get; set; }
        public string? ProtocolText { get; set; }
        public DateTime? Date { get; set; }
        public string? Relation { get; set; }
        public bool CanOpen { get; set; }
    }

    /// <summary>Documents related both ways (DocumentDocument), with the relation title; CanOpen when the user has a copy in one of their groups.</summary>
    private async Task<List<RelatedDocument>> Related(int id, CancellationToken ct) =>
        (await db.Query<RelatedRow>(
            $"""
            SELECT r.Id, d.Title, d.IdDocumentType AS Type,
                   (SELECT TOP 1 p.ProtocolText FROM Document.Protokollo p WHERE p.IdDocument = d.IdDocument ORDER BY p.IdProtokollo) AS ProtocolText,
                   (SELECT MIN(c.CreationDate) FROM Document.DocumentCopy c WHERE c.IdDocument = d.IdDocument) AS Date,
                   CASE WHEN r.Inverse = 1 THEN COALESCE(NULLIF(rt.ReverseTitle, ''), {Sql.Title("[Document].[RelationTypeTitleResources]", "rt.IdRelationType")})
                        ELSE {Sql.Title("[Document].[RelationTypeTitleResources]", "rt.IdRelationType")} END AS Relation,
                   CASE WHEN EXISTS (
                        SELECT 1 FROM Document.DocumentCopy c
                         INNER JOIN Organization.UserRoleGroupRelationship ur ON ur.IdOrgGroup IN (c.IdOwnedByGroup, c.IdCreatedByGroup)
                         WHERE c.IdDocument = d.IdDocument AND ur.IdOrgUser = @userId AND ur.Enabled = 1) THEN 1 ELSE 0 END AS CanOpen
              FROM (SELECT IdDocumentSxetiko AS Id, IdRelationType, 0 AS Inverse FROM Document.DocumentDocument WHERE IdDocument = @id
                    UNION SELECT IdDocument AS Id, IdRelationType, 1 AS Inverse FROM Document.DocumentDocument WHERE IdDocumentSxetiko = @id) r
              INNER JOIN Document.Document d ON d.IdDocument = r.Id AND d.IsDeleted = 0
              LEFT JOIN Document.RelationType rt ON rt.IdRelationType = r.IdRelationType
             ORDER BY r.Id
            """, new { id, userId = user.Required.Id }, ct))
        .Select(r => new RelatedDocument
        {
            Id = r.Id,
            Title = string.IsNullOrWhiteSpace(r.Title) ? "(χωρίς θέμα)" : r.Title,
            Type = r.Type,
            ProtocolText = r.ProtocolText,
            Date = Json.Iso(r.Date),
            Relation = r.Relation ?? "Σχετικό",
            CanOpen = r.CanOpen,
        }).ToList();

    private sealed class HistoryRow
    {
        public DateTime Date { get; set; }
        public string? Action { get; set; }
        public string? ActionTitle { get; set; }
        public string? IssuingUser { get; set; }
        public string? IssuingGroup { get; set; }
        public string? TargetUser { get; set; }
        public string? TargetGroup { get; set; }
        public string? Comment { get; set; }
    }

    /// <summary>«Ιστορικό» (UIViewHist): Document.History of every copy, newest first.</summary>
    public async Task<List<DocumentHistoryEntry>> History(int id, int groupId, CancellationToken ct)
    {
        await Accessible(id, groupId, ct);
        var rows = await db.Query<HistoryRow>(
            $"""
            SELECT h.Date, h.Action, h.Comment,
                   {Sql.Title("[Document].[HistoryActionTitleResources]", "h.IdHistoryAction")} AS ActionTitle,
                   NULLIF(LTRIM(RTRIM(ISNULL(iu.Surname, '') + ' ' + ISNULL(iu.Name, ''))), '') AS IssuingUser, ig.Name AS IssuingGroup,
                   NULLIF(LTRIM(RTRIM(ISNULL(tu.Surname, '') + ' ' + ISNULL(tu.Name, ''))), '') AS TargetUser, tg.Name AS TargetGroup
              FROM Document.History h
              INNER JOIN Document.DocumentCopy dc ON dc.IdDocumentCopy = h.IdDocumentCopy
              LEFT JOIN Organization.OrgUser iu ON iu.IdOrgUser = h.IdIssuingUser
              LEFT JOIN Organization.OrgGroup ig ON ig.IdOrgGroup = h.IdIssuingGroup
              LEFT JOIN Organization.OrgUser tu ON tu.IdOrgUser = h.IdTargetUser
              LEFT JOIN Organization.OrgGroup tg ON tg.IdOrgGroup = h.IdTargetGroup
             WHERE dc.IdDocument = @id
             ORDER BY h.Date DESC, h.IdHistory DESC
            """, new { id }, ct);
        return rows.Select(h => new DocumentHistoryEntry
        {
            At = Json.Iso(h.Date)!,
            Action = h.ActionTitle ?? h.Action ?? "",
            User = h.IssuingUser,
            Group = h.IssuingGroup,
            Target = h.TargetUser ?? h.TargetGroup,
            Comment = string.IsNullOrWhiteSpace(h.Comment) ? null : h.Comment,
        }).ToList();
    }

    /// <summary>`GET {id}/files`: every file of the document in one ZIP (DocumentDownloadFilesAction), folders per role.</summary>
    public async Task<IResult> Zip(int id, int groupId, CancellationToken ct)
    {
        await Accessible(id, groupId, ct);
        var title = await db.Scalar<string>("SELECT Title FROM Document.Document WHERE IdDocument = @id", new { id }, ct);
        var files = await Files(id, false, ct);
        if (files.Count == 0) throw new ApiException("Το έγγραφο δεν έχει αρχεία.");
        var folder = new Dictionary<string, string> { ["attachment"] = "Συνημμένα/", ["internal"] = "Εσωτερικά συνημμένα/", ["signer"] = "Συνημμένα υπογραφόντων/" };
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                using var res = await v1.Stream($"services/file/get/{f.Id}", ct);
                if (!res.IsSuccessStatusCode) throw new ApiException($"Το αρχείο «{f.Name}» δεν ήταν διαθέσιμο.");
                var path = folder.GetValueOrDefault(f.Role, "") + Safe(f.Name);
                for (var n = 2; !used.Add(path); n++) path = folder.GetValueOrDefault(f.Role, "") + $"{Path.GetFileNameWithoutExtension(f.Name)} ({n}){Path.GetExtension(f.Name)}";
                var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
                await using var to = entry.Open();
                await (await res.Content.ReadAsStreamAsync(ct)).CopyToAsync(to, ct);
            }
        }
        ms.Position = 0;
        var name = Safe(string.IsNullOrWhiteSpace(title) ? $"Έγγραφο {id}" : title.Length > 80 ? title[..80] : title) + ".zip";
        return Results.File(ms, "application/zip", name);
    }

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}
