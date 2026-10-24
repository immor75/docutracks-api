using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace Docutracks.Api.Documents;

/// <summary>
/// `POST actions`, `GET action-data/{code}`, `POST action`, `GET thematic-folders`. Availability from
/// v1 (CanExecute), forms built here from the Razor views of each action, execution by posting the
/// same form as the user (ClassicWriter → DocumentController → IUiAction.ExecuteAction).
/// </summary>
public sealed class ActionsService(V1Mobile mobile, V1Client v1, ClassicWriter writer, ViewService view, IDb db, IReference reference, ISettings settings, IMemoryCache cache)
{
    /// <summary>Actions this UI runs with a form of its own (the rest stay links to the classic UI).</summary>
    public static readonly HashSet<string> WithForm =
    [
        "UiArchive", "UiUnArchive", "UiCancel", "UiDelete", "UiUnAssign", "UiReturn", "UiFreeze", "UiUnfreeze",
        "UiAssign", "UiNotify", "UiInform", "UiChangeCreator", "UiSend", "UiSendToChild", "UiSendToFirstLevelChild",
    ];

    public async Task<DocumentActionsPayload> Actions(DocumentActionsRequest req, CancellationToken ct)
    {
        var ids = req.DocumentIds.Distinct().ToList();
        if (ids.Count == 0) throw ApiException.BadRequest("Δεν έχει οριστεί έγγραφο.");
        if (ids.Count > 200) throw ApiException.BadRequest("Έως 200 έγγραφα τη φορά.");
        // v1 answers one document per call; a few in parallel.
        using var gate = new SemaphoreSlim(6);
        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await mobile.Get(id, req.GroupId, ct);
                return new DocumentActions(id, r.CanSee ? r.Actions : []);
            }
            finally
            {
                gate.Release();
            }
        });
        return new DocumentActionsPayload((await Task.WhenAll(tasks)).ToList());
    }

    /* ---------- forms ---------- */

    public async Task<ActionForm> Form(string code, int groupId, List<int> documentIds, CancellationToken ct)
    {
        if (documentIds.Count == 0) throw ApiException.BadRequest("Δεν έχει οριστεί έγγραφο.");
        var first = documentIds[0];
        var (_, copy) = await view.Accessible(first, groupId, ct);
        if (!WithForm.Contains(code))
            return new ActionForm { Code = code, Title = code, Submit = "", Classic = "Η ενέργεια γίνεται ακόμη στο κλασικό UI." };

        var max = await settings.Int("MaxCommentLength", 1000, ct);
        ActionField Comment(bool required = false, string? message = null) =>
            new() { Kind = "comment", Key = "Comment", Label = "Σχόλιο", MaxLength = max, Required = required ? true : null, RequiredMessage = message };
        var type = await db.Scalar<int>("SELECT IdDocumentType FROM Document.Document WHERE IdDocument = @first", new { first }, ct);

        switch (code)
        {
            case "UiArchive":
            {
                var fields = new List<ActionField>();
                if (await settings.Bool("UiArchiveDisplayThematicFolder", true, ct))
                {
                    var required = await settings.Bool("ArchiveActionRequiresThematicFolder", false, ct);
                    fields.Add(new ActionField
                    {
                        Kind = "thematicFolders", Key = "ThematicFolders", Label = "Θεματικοί Φάκελοι",
                        AllowNew = await settings.Bool("AllowNewThematicalFolderByUsers", true, ct),
                        Required = required ? true : null, RequiredMessage = "Δεν έχει επιλεγεί θεματικός φάκελος",
                        Value = documentIds.Count == 1 ? await CopyFolders(copy.IdDocumentCopy, ct) : new List<ThematicFolder>(),
                    });
                }
                fields.Add(Comment());
                return new ActionForm
                {
                    Code = code, Title = "Αρχειοθέτηση", Submit = "Αρχειοθέτηση", Fields = fields,
                    Message = "Είστε σίγουρος ότι θέλετε να αρχειοθετήσετε το συγκεκριμένο έγγραφο;",
                };
            }
            case "UiUnArchive":
                return new ActionForm
                {
                    Code = code, Title = "Επαναφορά από Αρχειοθέτηση", Submit = "Επαναφορά από Αρχειοθέτηση", Fields = [Comment()],
                    Message = "Είστε σίγουρος ότι θέλετε να επαναφέρετε σε κατάσταση Δημιουργήθηκε το συγκεκριμένο έγγραφο;",
                };
            case "UiCancel":
                return new ActionForm
                {
                    Code = code, Title = "Ακύρωση", Submit = "Συνέχεια",
                    Message = "Είστε σίγουρος πως επιθυμείτε να ακυρώσετε αυτό το έγγραφο;",
                    Fields = [Comment(true, "Η ενέργεια της ακύρωσης απαιτεί αιτιολόγηση.")],
                };
            case "UiDelete":
                return new ActionForm
                {
                    Code = code, Title = "Διαγραφή", Submit = "Διαγραφή", Danger = true, Fields = [],
                    Message = "Προσοχή. Μετά την ολοκλήρωση αυτής της ενέργειας, το έγγραφο θα διαγραφεί οριστικά από τον κατάλογο εγγράφων.",
                    Warnings = copy.Status == 3 ? ["Σας ενημερώνουμε οτι το έγγραφο έχει διακινηθεί εξωτερικά ή εσωτερικά. Επιθυμείτε να προχωρήσετε στην ενέργεια?"] : null,
                };
            case "UiUnAssign":
                return new ActionForm
                {
                    Code = code, Title = "Αποχρέωση", Submit = "Αποχρέωση", Fields = [Comment()],
                    Message = "Είστε σίγουρος ότι θέλετε να αποχρεωθείτε το έγγραφο;",
                };
            case "UiFreeze":
                return new ActionForm
                {
                    Code = code, Title = "Πάγωμα", Submit = "Πάγωμα", Fields = [Comment()],
                    Message = "Είστε σίγουρος ότι θέλετε να παγώσετε το συγκεκριμένο έγγραφο;",
                };
            case "UiUnfreeze":
                return new ActionForm
                {
                    Code = code, Title = "Ξεπάγωμα", Submit = "Ξεπάγωμα", Fields = [Comment()],
                    Message = "Είστε σίγουρος ότι θέλετε να επαναφέρετε το συγκεκριμένο έγγραφο;",
                };
            case "UiReturn":
                return type == DocumentType.Draft ? await ReturnDraft(first, Comment(), ct) : await ReturnIncoming(Comment(), ct);
            case "UiAssign":
            {
                var tree = await OrgTree(groupId, ct);
                return new ActionForm
                {
                    Code = code, Title = "Χρέωση", Submit = "Χρέωση",
                    Warnings = documentIds.Count == 1 && copy.AssignedTo is not null
                        ? [$"Το έγγραφο είναι ήδη χρεωμένο σε: Υ.Μ {await reference.GroupName(groupId, ct)} ( {copy.AssignedTo} )"]
                        : null,
                    Fields =
                    [
                        new() { Kind = "people", Key = "Assignee", Label = "Στον χρήστη", Pick = "users", Multi = false, Required = true, RequiredMessage = "Επιλέξτε χρήστη.", Tree = Cut(tree, 1) },
                        new() { Kind = "people", Key = "ExtraAssignees", Label = "Συγχρέωση", Pick = "users", Multi = true, Tree = Cut(tree, 0) },
                        new() { Kind = "checkbox", Key = "Urgent", Label = "Χειρισμός κατά προτεραιότητα", Value = copy.IsUrgent },
                        .. await settings.Bool("IsForAnswerEnabled", false, ct)
                            ? new[] { new ActionField { Kind = "checkbox", Key = "ForAnswer", Label = "Χρέωση προς Απάντηση", Value = false } }
                            : [],
                        Comment(),
                    ],
                };
            }
            case "UiNotify":
            case "UiInform":
                return new ActionForm
                {
                    Code = code, Title = code == "UiNotify" ? "Γνωστοποίηση" : "Ενημέρωση", Submit = code == "UiNotify" ? "Γνωστοποίηση" : "Ενημέρωση",
                    Fields =
                    [
                        new()
                        {
                            Kind = "people", Key = "Users", Label = code == "UiNotify" ? "Αναζήτηση Εσωτερικού Αποδέκτη" : "Χρήστες προς ενημέρωση",
                            Pick = "users", Multi = true, Required = true, RequiredMessage = "Παρακαλώ επιλέξτε τουλάχιστον έναν αποδέκτη.",
                            Tree = new JsonNodeValue(await OrgTree(await RootGroup(ct), ct)),
                        },
                        Comment(),
                    ],
                };
            case "UiChangeCreator":
            {
                var root = await OrgTree(await RootGroup(ct), ct);
                var fields = new List<ActionField>
                {
                    new()
                    {
                        Kind = "people", Key = "CreatedBy", Label = "Καταχωριστής", Pick = "users", Multi = false, Required = true, Tree = new JsonNodeValue(root),
                        Hint = "Η υπηρεσία στην οποία τον επιλέγετε γίνεται Υπηρεσιακή Μονάδα Καταχώρισης.",
                    },
                };
                if (type == DocumentType.Outgoing)
                    fields.Add(new() { Kind = "people", Key = "CreatedForGroup", Label = "Εκδούσα Υπηρεσιακή Μονάδα", Pick = "groups", Multi = false, Tree = GroupsOnly(root) });
                return new ActionForm
                {
                    Code = code, Title = "Αλλαγή Εκδούσας Μονάδας και Καταχωριστή", Submit = "Αποθήκευση", Fields = fields,
                    Message = "Η εκτέλεση της ενέργειας θα ενημερώσει τις πληροφορίες του εγγράφου σχετικά με την Εκδούσα Υπηρεσιακή Μονάδα (μόνο για εξερχόμενα έγγραφα), την Υπηρεσιακή Μονάδα Καταχώρισης και τον Καταχωριστή.",
                    Warnings = ["Αν η επιλεγμένη υπηρεσία καταχώρισης έχει διαφορετική υπηρεσία διεκπεραίωσης από την αρχική, δε θα είναι δυνατή η εκ νέου εκτέλεση της Αλλαγής."],
                };
            }
            case "UiSend":
            case "UiSendToChild":
            case "UiSendToFirstLevelChild":
            {
                var tree = code == "UiSend" ? await OrgTree(await RootGroup(ct), ct) : await OrgTree(groupId, ct);
                if (code == "UiSendToFirstLevelChild") tree = Cut(tree, 1).Node ?? new JsonArray();
                var hasExternalEmail = await db.Scalar<int>(
                    """
                    SELECT COUNT(1) FROM Document.DocumentContact dc
                     INNER JOIN Contact.ContactInfo ci ON ci.IdContactInfo = dc.IdContactInfo
                     WHERE dc.IdDocument = @first AND dc.IdContactRelation IN (1, 2) AND dc.IdContactInfo IS NOT NULL
                    """, new { first }, ct) > 0;
                return new ActionForm
                {
                    Code = code, Submit = "Αποστολή",
                    Title = code switch { "UiSend" => "Αποστολή", "UiSendToChild" => "Αποστολή σε τμήμα", _ => "Αποστολή σε Υφιστάμενες Υπηρεσίες" },
                    Fields =
                    [
                        new()
                        {
                            Kind = "people", Key = "Groups", Label = "Αναζήτηση Εσωτερικού Αποδέκτη", Pick = "groups", Multi = true, Required = true,
                            RequiredMessage = "Παρακαλώ επιλέξτε τουλάχιστον έναν αποδέκτη.", Tree = GroupsOnly(tree),
                            PerItem = new PerItem { EnergeiaTypes = (await EnergeiaTypes(ct)).ToList(), DefaultEnergeiaType = 1, Urgent = true },
                        },
                        Comment(),
                    ],
                    // Outgoing with external recipients by e-mail (CustomEmail): the classic UI for now.
                    Classic = type == DocumentType.Outgoing && hasExternalEmail
                        ? "Το έγγραφο έχει εξωτερικούς αποδέκτες με e-mail· η αποστολή τους γίνεται ακόμη στο κλασικό UI."
                        : null,
                };
            }
        }
        throw ApiException.BadRequest("Άγνωστη ενέργεια.");
    }

    private async Task<ActionForm> ReturnIncoming(ActionField comment, CancellationToken ct) => new()
    {
        Code = "UiReturn", Title = "Επιστροφή στον αποστολέα", Submit = "Επιστροφή",
        Fields =
        [
            new()
            {
                Kind = "select", Key = "EnergeiaTypeId", Label = "Ενέργεια", Required = true, RequiredMessage = "Επιλέξτε ενέργεια.",
                Options = (await EnergeiaTypes(ct)).Select(o => new SelectOption { Id = o.Id, Label = o.Label }).ToList(),
            },
            comment,
        ],
    };

    private sealed class SignerRow
    {
        public int IdSignature { get; set; }
        public string? Name { get; set; }
        public bool HasSigned { get; set; }
        public bool HasManager { get; set; }
    }

    /// <summary>Return of a draft (ReturnDraftTo): to the author (0) or to a signature already given.</summary>
    private async Task<ActionForm> ReturnDraft(int id, ActionField comment, CancellationToken ct)
    {
        var author = await db.Scalar<string>(
            "SELECT NULLIF(LTRIM(RTRIM(ISNULL(u.Surname, '') + ' ' + ISNULL(u.Name, ''))), '') FROM Document.Document d LEFT JOIN Organization.OrgUser u ON u.IdOrgUser = d.IdAuthor WHERE d.IdDocument = @id",
            new { id }, ct);
        var signers = await db.Query<SignerRow>(
            """
            SELECT s.IdSignature, s.HasSigned, COALESCE(g.Name, NULLIF(LTRIM(RTRIM(ISNULL(u.Surname, '') + ' ' + ISNULL(u.Name, ''))), '')) AS Name,
                   CASE WHEN g.IdOrgGroup IS NULL OR g.IdActiveManager IS NOT NULL THEN 1 ELSE 0 END AS HasManager
              FROM Document.Signature s
              LEFT JOIN Organization.OrgGroup g ON g.IdOrgGroup = s.IdOrgGroup
              LEFT JOIN Organization.OrgUser u ON u.IdOrgUser = s.IdOrgUser
             WHERE s.IdDocument = @id AND s.HasSigned = 1
             ORDER BY s.IdSignature
            """, new { id }, ct);
        var options = new List<SelectOption> { new() { Id = 0, Label = $"Συντάκτης ({author ?? "—"})" } };
        options.AddRange(signers.Select(s => new SelectOption
        {
            Id = s.IdSignature, Label = s.Name ?? $"Υπογραφή {s.IdSignature}",
            Disabled = s.HasManager ? null : true, Reason = s.HasManager ? null : "Η υπηρεσία δεν έχει ενεργό προϊστάμενο.",
        }));
        return new ActionForm
        {
            Code = "UiReturn", Title = "Επιστροφή σχεδίου", Submit = "Επιστροφή",
            Fields = [new() { Kind = "select", Key = "SignatureId", Label = "Υπογραφές", Required = true, Options = options, Value = 0 }, comment],
        };
    }

    private async Task<IReadOnlyList<Option>> EnergeiaTypes(CancellationToken ct) =>
        await reference.Titles("Document.EnergeiaType", "IdEnergeiaType", "[Document].[EnergeiaTypeTitleResources]", ct,
            "t.IsActive = 1 AND (t.ValidUntil IS NULL OR t.ValidUntil > GETDATE())");

    private sealed class FolderRow
    {
        public int IdFolder { get; set; }
        public string? Name { get; set; }
        public int? FolderParentId { get; set; }
    }

    private async Task<List<ThematicFolder>> CopyFolders(int copyId, CancellationToken ct) =>
        (await db.Query<FolderRow>(
            "SELECT f.IdFolder, f.Name, f.FolderParentId FROM Document.DocumentCopyFolder dcf INNER JOIN Document.Folder f ON f.IdFolder = dcf.IdFolder WHERE dcf.IdDocumentCopy = @copyId",
            new { copyId }, ct)).Select(f => new ThematicFolder { Id = f.IdFolder, Name = f.Name ?? "" }).ToList();

    /// <summary>Thematic folders the group may use (central ones and its own), matching the text, with their path.</summary>
    public async Task<List<ThematicFolder>> ThematicFolders(int groupId, string? search, CancellationToken ct)
    {
        var all = await cache.GetOrCreateAsync($"thematic:{groupId}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            return await db.Query<FolderRow>(
                """
                SELECT f.IdFolder, f.Name, f.FolderParentId FROM Document.Folder f
                 WHERE f.IsCentral = 1 OR f.IdOrgGroup = @groupId
                    OR EXISTS (SELECT 1 FROM Document.FolderOwnerGroup o WHERE o.IdFolder = f.IdFolder AND o.IdOrgGroup = @groupId)
                """, new { groupId }, ct);
        }) ?? [];
        var byId = all.ToDictionary(f => f.IdFolder);
        string? PathOf(FolderRow f)
        {
            var parts = new List<string>();
            for (var p = f.FolderParentId; p is int pid && byId.TryGetValue(pid, out var parent) && parts.Count < 10; p = parent.FolderParentId)
                parts.Insert(0, parent.Name ?? "");
            return parts.Count == 0 ? null : string.Join(" › ", parts);
        }
        var q = Fold(search);
        return all.Where(f => q.Length == 0 || Fold(f.Name).Contains(q))
            .OrderBy(f => f.Name, StringComparer.CurrentCulture)
            .Take(20)
            .Select(f => new ThematicFolder { Id = f.IdFolder, Name = f.Name ?? "", Path = PathOf(f) })
            .ToList();
    }

    /// <summary>Lower case without accents, for matching Greek text.</summary>
    internal static string Fold(string? s) =>
        new string((s ?? "").Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant().Trim();

    /* ---------- organisation ---------- */

    /// <summary>The top of the organisation (the group without parent the tree starts from; the protocol is 1).</summary>
    private async Task<int> RootGroup(CancellationToken ct) =>
        await cache.GetOrCreateAsync("rootgroup", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            return await db.Scalar<int?>("SELECT TOP 1 IdOrgGroup FROM Organization.OrgGroup WHERE IdParentOrgGroup IS NULL AND IsActive = 1 ORDER BY IdOrgGroup", ct: ct) ?? 1;
        });

    /// <summary>The organisation tree from a group down (v1 get-organization-tree), cached for 5 minutes.</summary>
    internal async Task<JsonNode> OrgTree(int groupId, CancellationToken ct) =>
        (await cache.GetOrCreateAsync($"orgtree:{groupId}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await v1.Json(HttpMethod.Post, "services/organization/get-organization-tree", new { Ids = new[] { groupId }, Codes = Array.Empty<string>() }, ct)
                   ?? new JsonArray();
        }))!.DeepClone();

    private static JsonNodeValue Cut(JsonNode tree, int depth)
    {
        var copy = tree.DeepClone();
        void Walk(JsonNode? n, int d)
        {
            if (n is not JsonObject o) return;
            if (d <= 0) o["SubGroups"] = new JsonArray();
            else foreach (var c in o["SubGroups"]?.AsArray() ?? []) Walk(c, d - 1);
        }
        foreach (var n in copy.AsArray()) Walk(n, depth);
        return new JsonNodeValue(copy);
    }

    private static JsonNodeValue Cut(JsonNodeValue tree, int depth) => Cut(tree.Node ?? new JsonArray(), depth);

    private static JsonNodeValue GroupsOnly(JsonNode tree)
    {
        var copy = tree.DeepClone();
        void Walk(JsonNode? n)
        {
            if (n is not JsonObject o) return;
            o["Users"] = new JsonArray();
            foreach (var c in o["SubGroups"]?.AsArray() ?? []) Walk(c);
        }
        foreach (var n in copy.AsArray()) Walk(n);
        return new JsonNodeValue(copy);
    }

    private static JsonNodeValue GroupsOnly(JsonNodeValue tree) => GroupsOnly(tree.Node ?? new JsonArray());

    /* ---------- run ---------- */

    private sealed class RunDocRow
    {
        public int IdDocumentType { get; set; }
        public int? IdCreatedByUser { get; set; }
    }

    /// <summary>
    /// `POST action`: the values become the fields the Razor form of the action posts, and the form is
    /// posted as the user (DocumentModelBinder → IUiAction.ExecuteAction, which checks CanExecute again).
    /// One request per document, so that a failure does not undo the others.
    /// </summary>
    public async Task<RunActionPayload> Run(RunActionRequest req, CancellationToken ct)
    {
        var ids = req.DocumentIds.Distinct().ToList();
        if (ids.Count == 0) throw ApiException.BadRequest("Δεν έχει οριστεί έγγραφο.");
        if (ids.Count > 200) throw ApiException.BadRequest("Έως 200 έγγραφα τη φορά.");
        if (!WithForm.Contains(req.Code)) throw ApiException.BadRequest("Η ενέργεια δεν υποστηρίζεται από αυτό το endpoint.");
        if (ids.Count > 1 && !V1Mobile.Mass.Contains(req.Code)) throw ApiException.BadRequest("Η ενέργεια δεν γίνεται μαζικά.");
        Require(req.Code, req.Values);

        var results = new List<ActionResult>();
        foreach (var id in ids)
        {
            try
            {
                var doc = await db.First<RunDocRow>("SELECT IdDocumentType, IdCreatedByUser FROM Document.Document WHERE IdDocument = @id", new { id }, ct)
                          ?? throw new ApiException("Το έγγραφο δεν βρέθηκε.");
                var fields = ActionFields(req.Code, req.Values, doc.IdDocumentType, doc.IdCreatedByUser);
                var r = await writer.Action(req.GroupId, id, req.Code, fields, ct);
                results.Add(new ActionResult { DocumentId = id, Success = r.Success, Message = r.Message });
            }
            catch (ApiException e) when (e.Status != System.Net.HttpStatusCode.Unauthorized)
            {
                results.Add(new ActionResult { DocumentId = id, Success = false, Message = e.Message });
            }
        }
        return new RunActionPayload(results);
    }

    private static void Require(string code, Dictionary<string, JsonElement> v)
    {
        string? missing = code switch
        {
            "UiAssign" when RazorFields.List<PersonValue>(v, "Assignee").Count == 0 => "Επιλέξτε χρήστη.",
            "UiNotify" or "UiInform" when RazorFields.List<PersonValue>(v, "Users").Count == 0 => "Παρακαλώ επιλέξτε τουλάχιστον έναν αποδέκτη.",
            "UiSend" or "UiSendToChild" or "UiSendToFirstLevelChild" when RazorFields.List<PersonValue>(v, "Groups").Count == 0 => "Παρακαλώ επιλέξτε τουλάχιστον έναν αποδέκτη.",
            "UiCancel" when string.IsNullOrWhiteSpace(RazorFields.Text(v, "Comment")) => "Η ενέργεια της ακύρωσης απαιτεί αιτιολόγηση.",
            "UiChangeCreator" when RazorFields.List<PersonValue>(v, "CreatedBy").All(p => p.UserId is null) => "Επιλέξτε καταχωριστή.",
            _ => null,
        };
        if (missing is not null) throw new ApiException(missing);
    }

    /// <summary>The fields of UiActions/UiViewDoc/{code}.cshtml.</summary>
    internal static RazorFields ActionFields(string code, Dictionary<string, JsonElement> v, int documentType, int? createdBy)
    {
        const string P = Classic.Params;
        var f = new RazorFields();
        var comment = RazorFields.Text(v, "Comment") ?? "";
        switch (code)
        {
            case "UiArchive":
                f.Folders(RazorFields.List<ThematicFolder>(v, "ThematicFolders"));
                f.Add(P + "Comment.Comment", comment);
                break;
            case "UiUnArchive" or "UiCancel" or "UiUnAssign" or "UiFreeze" or "UiUnfreeze":
                f.Add(P + "Comment.Comment", comment);
                break;
            case "UiDelete":
                break;
            case "UiAssign":
                f.AddAll(P + "Kinisi.UserReceivers.Id", RazorFields.List<PersonValue>(v, "Assignee").Where(p => p.UserId is not null).Take(1).Select(RazorFields.UserInGroup));
                f.AddAll(P + "Kinisi.SecondaryChargeReceivers.Id", RazorFields.List<PersonValue>(v, "ExtraAssignees").Where(p => p.UserId is not null).Select(RazorFields.UserInGroup));
                if (RazorFields.Bool(v, "Urgent")) f.Add(P + "Kinisi.UrgentFlags", true);
                if (RazorFields.Bool(v, "ForAnswer")) f.Add(P + "Kinisi.IsForAnswerFlags", true);
                f.Add(P + "Kinisi.Kinds.Id", Classic.KinisiSend);
                f.Add(P + "Comment.Comment", comment);
                break;
            case "UiNotify":
            case "UiInform":
                f.AddAll(P + "Kinisi.UserReceivers.Id", RazorFields.List<PersonValue>(v, "Users").Where(p => p.UserId is not null).Select(RazorFields.UserInGroup));
                if (code == "UiInform") f.Add(P + "Kinisi.Kinds.Id", Classic.KinisiInform);
                f.Add(P + "Comment.Comment", comment);
                break;
            case "UiChangeCreator":
            {
                var by = RazorFields.List<PersonValue>(v, "CreatedBy").First(p => p.UserId is not null);
                f.Add(P + "CreatedBy.CreatedBy", by.UserId);
                f.Add(P + "CreatedByGroup.CreatedByGroup", by.GroupId);
                if (RazorFields.List<PersonValue>(v, "CreatedForGroup").FirstOrDefault() is { } forGroup)
                    f.Add(P + "CreatedForGroup.CreatedForGroup", forGroup.GroupId);
                break;
            }
            case "UiSend":
            case "UiSendToChild":
            case "UiSendToFirstLevelChild":
                // One row per group: receiver, kind of action, urgent, comment (the same comment for all).
                foreach (var g in RazorFields.List<PersonValue>(v, "Groups"))
                {
                    f.Add(P + "Kinisi.GroupReceivers.Id", g.GroupId);
                    f.Add(P + "Kinisi.EnergeiaTypes.Id", g.EnergeiaTypeId ?? 1);
                    f.Add(P + "Kinisi.Kinds.Id", Classic.KinisiSend);
                    f.Add(P + "Kinisi.UrgentFlags", g.Urgent == true);
                    f.Add(P + "Kinisi.Comments", comment);
                }
                break;
            case "UiReturn":
                f.Add(P + "Kinisi.Kinds.Id", Classic.KinisiReturn);
                f.Add(P + "Kinisi.UserReceiver.Id", createdBy);
                if (documentType == DocumentType.Draft)
                {
                    f.Add(P + "Kinisi.EnergeiaTypes.Id", Classic.EnergeiaNone);
                    f.Add(P + "SignerToReturnDraft.SignatureId", RazorFields.Int(v, "SignatureId") ?? 0);
                    f.Add(P + "ReturnAttachments.HasAttachments", false);
                }
                else
                {
                    f.Add(P + "Kinisi.EnergeiaTypes.Id", RazorFields.Int(v, "EnergeiaTypeId") ?? throw new ApiException("Επιλέξτε ενέργεια."));
                }
                f.Add(P + "Kinisi.Comments", comment);
                break;
            default:
                throw ApiException.BadRequest("Άγνωστη ενέργεια.");
        }
        return f;
    }
}
