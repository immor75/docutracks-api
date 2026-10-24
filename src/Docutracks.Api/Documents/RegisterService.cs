using System.Text.Json;
using System.Text.Json.Nodes;

namespace Docutracks.Api.Documents;

/// <summary>
/// «Νέο έγγραφο» and «Επεξεργασία» (phase 8): types, forms, kinds, temporary files, validation;
/// the registration itself, the edit and the signature chain post the forms of the Razor editor
/// (ClassicWriter), so the same code runs (DocumentModelBinder, Register/Edit actions, GetCalculatedSignature).
/// </summary>
public sealed class RegisterService(
    IDb db, IReference reference, ISettings settings, RegisterForms forms, DocumentValues values, ViewService view,
    V1Mobile mobile, ClassicWriter writer, TempFiles temp, UserContext user)
{
    /* ---------- types ---------- */

    private sealed class TypeRow
    {
        public int IdNewDocType { get; set; }
        public string? Title { get; set; }
        public string? Mapping { get; set; }
    }

    /// <summary>Types of UINewSelection after roles and settings; with a source, those of an answer or a clone.</summary>
    public async Task<NewTypesPayload> NewTypes(int groupId, int? from, string? mode, CancellationToken ct)
    {
        var me = user.Required;
        var role = await reference.Role(me.Id, groupId, ct);
        var rows = await db.Query<TypeRow>(
            $"SELECT t.IdNewDocType, t.Mapping, {Sql.Title("[Document].[NewDocTypeTitleResources]", "t.IdNewDocType")} AS Title FROM Document.NewDocType t WHERE t.Active = 1 ORDER BY t.OrderBy",
            ct: ct);
        var forceSigned = await settings.Bool("ForceSignedOutgoing", false, ct);
        var hasToken = await db.Scalar<int>(
            "SELECT COUNT(1) FROM Organization.OrgUserDigitalSignatureToken WHERE IdOrgUser = @id AND IdDigitalSignatureToken NOT IN (11, 12)", new { id = me.Id }, ct) > 0;
        var outgoing = role.Has(Roles.OutgoingRegistrar) && (!forceSigned || hasToken);
        var incoming = role.Has(Roles.IncomingRegistrar) || role.Has(Roles.IncomingRegistrarWithFile);
        if (await settings.Bool("OnlyAllowProtocolUsersToRegisterIncoming", false, ct) && !role.IsProtocolUser) incoming = false;

        var types = rows.Where(t =>
                t.IdNewDocType switch
                {
                    7 => outgoing,
                    1 => incoming,
                    6 => !role.IsProtocolUser,
                    _ => !(role.IsProtocolUser && t.IdNewDocType != 5),
                })
            .Select(t => new NewDocType
            {
                Id = t.IdNewDocType,
                Title = t.Title ?? t.IdNewDocType.ToString(),
                DocumentType = RegisterForms.DocumentTypeOf.GetValueOrDefault(t.IdNewDocType, int.TryParse(t.Mapping, out var m) ? m : 0),
            })
            .ToList();

        if (from is null) return new NewTypesPayload { Types = types, AllowOld = role.IsActiveOldProtokollo };

        var src = await Source(from.Value, groupId, mode, ct);
        var allowed = mode == "answer"
            // UiNew/UiAnswer: not FEA, DECISION, INCOMING.
            ? new HashSet<int> { 6, 7, 5 }
            : src.Type == DocumentType.Outgoing ? [7, 6] : [RegisterForms.NewTypeOf.GetValueOrDefault(src.Type)];
        return new NewTypesPayload
        {
            Types = types.Where(t => allowed.Contains(t.Id)).ToList(),
            AllowOld = false,
            Source = new SourceDocument { Id = src.Id, Title = src.Title, ProtocolText = src.ProtocolText },
        };
    }

    private sealed record Src(int Id, string Title, string? ProtocolText, int Type);

    /// <summary>The answered or cloned document: visible to the user and the action allowed for it.</summary>
    private async Task<Src> Source(int id, int groupId, string? mode, CancellationToken ct)
    {
        if (mode is not ("answer" or "clone")) throw ApiException.BadRequest("Άγνωστη ενέργεια.");
        var actions = await mobile.Get(id, groupId, ct);
        if (!actions.CanSee) throw ApiException.NotFound();
        var code = mode == "answer" ? "UiAnswer" : "UiClone";
        var a = actions.Actions.FirstOrDefault(x => x.Code == code);
        if (a is not { Enabled: true }) throw new ApiException(a?.Reason ?? "Η ενέργεια δεν είναι διαθέσιμη.");
        var v = await view.View(id, groupId, ct);
        return new Src(id, v.Title, v.ProtocolText, v.Type);
    }

    /* ---------- forms ---------- */

    public async Task<RegisterForm> Form(int groupId, int newType, bool old, int? from, string? mode, CancellationToken ct)
    {
        var types = await NewTypes(groupId, from, mode, ct);
        if (types.Types.All(t => t.Id != newType))
            throw new ApiException(from is null ? "Δεν έχετε δικαίωμα καταχώρισης αυτού του τύπου." : "Το έγγραφο δεν μπορεί να γίνει αυτού του τύπου.");
        var form = await forms.Build(groupId, newType, old && from is null, ct);
        if (from is null) return form;

        var src = types.Source!;
        var srcValues = (await values.Of(src.Id, groupId, ct)).Values;
        var reference = $"«{src.Title}»{(src.ProtocolText is null ? "" : $" (Αριθμ. Πρωτ. {src.ProtocolText})")}";
        Dictionary<string, object?> prefill;
        if (mode == "answer")
        {
            prefill = new()
            {
                ["Related"] = new[] { new { Id = src.Id, Title = src.Title } },
                ["OtherRecipients"] = srcValues.GetValueOrDefault("Sender"),
                ["Categories"] = srcValues.GetValueOrDefault("Categories"),
            };
            form.Notice = $"Απάντηση στο {reference}. Συμπληρώθηκαν τα σχετικά έγγραφα, ο αποδέκτης (ο αποστολέας του εισερχομένου) και οι κατηγορίες.";
        }
        else
        {
            // DocumentClone.CloneDocument: everything but files, related documents, protocol and signatures.
            var skip = new HashSet<string> { "MainFile", "Attachments", "InternalAttachments", "SignerAttachments", "Related", "OldProtocolNumber", "OldProtocolDate",
                "FinalSigners", "ExtraCosigners", "CoAuthors", "Ratifiers", "ExternalSigners", "Author", "CreatedForGroup" };
            prefill = srcValues.Where(kv => !skip.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            form.Notice = $"Αντίγραφο του {reference}. Αντιγράφηκαν τα στοιχεία του, εκτός από τα αρχεία, τα σχετικά έγγραφα, το πρωτόκολλο και τις υπογραφές.";
        }
        foreach (var f in form.Fields)
            if (f.Kind != "display" && prefill.TryGetValue(f.Key, out var value) && value is not null) f.Value = value;
        var label = form.Title[(form.Title.IndexOf('(') + 1)..].TrimEnd(')');
        form.Title = $"{(mode == "answer" ? "Απάντηση" : "Κλωνοποίηση")}: {label}";
        form.Source = src;
        form.Mode = mode;
        return form;
    }

    /// <summary>Fields that do not change after protocol (CondIsLimitedEdit, DocumentPropertiesEditor).</summary>
    private static readonly HashSet<string> Limited =
        ["MainFile", "KindId", "Sender", "SenderProtocol", "SendDate", "Related", "ProtocolBook", "AltNumbering", "OldProtocolNumber", "OldProtocolDate", "Oikothen"];

    /// <summary>«Επεξεργασία»: the form of the document's type with its values; limited after protocol.</summary>
    public async Task<RegisterForm> EditForm(int id, int groupId, CancellationToken ct)
    {
        await EnsureEdit(id, groupId, ct);
        var current = await values.Of(id, groupId, ct);
        var newType = RegisterForms.NewTypeOf.GetValueOrDefault(current.DocumentType);
        var form = await forms.Build(groupId, newType, false, ct);
        var limited = current.DocumentType != DocumentType.Draft && current.HasProtocol;
        form.Fields = form.Fields.Where(f => f.Key is not ("SaveTitle" or "FromTemplate")).ToList();
        foreach (var f in form.Fields)
        {
            if (f.HiddenIf?.Key == "FromTemplate") f.HiddenIf = null;
            if (limited && Limited.Contains(f.Key)) f.ReadOnly = true;
            if (f.Kind != "display" && current.Values.TryGetValue(f.Key, out var v)) f.Value = v;
        }
        var label = form.Title[(form.Title.IndexOf('(') + 1)..].TrimEnd(')');
        form.Title = $"Επεξεργασία Εγγράφου ({label})";
        form.AllowBrief = false;
        form.BriefDefault = false;
        form.DocumentId = id;
        form.Notice = limited
            ? $"Περιορισμένη επεξεργασία: το έγγραφο έχει Αριθμ. Πρωτ. {current.ProtocolText}. Το κυρίως έγγραφο, το είδος, ο αποστολέας, τα σχετικά έγγραφα και τα στοιχεία πρωτοκόλλου δεν αλλάζουν."
            : null;
        return form;
    }

    private async Task EnsureEdit(int id, int groupId, CancellationToken ct)
    {
        var r = await mobile.Get(id, groupId, ct);
        if (!r.CanSee) throw ApiException.NotFound();
        var a = r.Actions.FirstOrDefault(x => x.Code == "UiEdit");
        if (a is not { Enabled: true }) throw new ApiException(a?.Reason ?? "Η επεξεργασία δεν είναι διαθέσιμη.");
    }

    /* ---------- kinds ---------- */

    /// <summary>Kinds enabled for a document type, as a tree; HasForm when the kind has «Ειδικά πεδία» (DocumentKindMetadata).</summary>
    public async Task<List<TreeOption>> Kinds(int type, CancellationToken ct)
    {
        var all = await reference.Kinds(ct);
        bool Enabled(KindRow k) => type switch
        {
            DocumentType.Incoming => k.EnabledForIncoming != false,
            DocumentType.Outgoing => k.EnabledForOutgoing != false,
            DocumentType.Draft => k.EnabledForSxedio != false,
            _ => true,
        };
        var enabled = all.Where(Enabled).ToList();
        // Keep the parents of enabled kinds, so the tree stays whole.
        var byId = all.ToDictionary(k => k.IdDocumentKind);
        var keep = enabled.Select(k => k.IdDocumentKind).ToHashSet();
        foreach (var k in enabled)
            for (var p = k.ParentId; p is int pid && byId.TryGetValue(pid, out var parent) && keep.Add(pid); p = parent.ParentId) { }
        return Trees.Build(all.Where(k => keep.Contains(k.IdDocumentKind)), k => k.IdDocumentKind, k => k.ParentId,
            k => k.Title ?? k.Code ?? k.IdDocumentKind.ToString(), k => k.IdDocumentKindMetadata is not null);
    }

    /// <summary>The Formio form of a kind (DocumentKindMetadata.MetadataSchema).</summary>
    public async Task<JsonNode> KindForm(int kindId, CancellationToken ct)
    {
        var schema = await db.Scalar<string>(
            """
            SELECT m.MetadataSchema FROM Document.DocumentKind k
              INNER JOIN Document.DocumentKindMetadata m ON m.IdDocumentKindMetadata = k.IdDocumentKindMetadata
             WHERE k.IdDocumentKind = @kindId
            """, new { kindId }, ct);
        if (string.IsNullOrWhiteSpace(schema)) throw new ApiException("Το είδος δεν έχει ειδικά πεδία.");
        var node = JsonNode.Parse(schema) ?? throw new ApiException("Τα ειδικά πεδία του είδους δεν διαβάστηκαν.");
        // Stored either as a whole form ({ components }) or as the list of components.
        return node is JsonArray arr ? new JsonObject { ["components"] = arr } : node;
    }

    /* ---------- validation ---------- */

    private static bool IsEmpty(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()),
        JsonValueKind.Array => v.GetArrayLength() == 0,
        _ => false,
    };

    private static bool Hidden(RegisterField f, Dictionary<string, JsonElement> values)
    {
        if (f.HiddenIf is null) return false;
        var actual = values.TryGetValue(f.HiddenIf.Key, out var v) ? v.GetRawText() : "null";
        return actual == JsonSerializer.Serialize(f.HiddenIf.Value);
    }

    /// <summary>As DocumentPropertiesEditor validates: required fields on screen, lengths, files that exist and belong to the user.</summary>
    internal List<FieldError> Validate(RegisterForm form, Dictionary<string, JsonElement> values, bool brief)
    {
        var errors = new List<FieldError>();
        var me = user.Required.Id;
        foreach (var f in form.Fields)
        {
            if (brief && f.Brief != true) continue;
            if (f.ReadOnly == true || Hidden(f, values) || f.Kind is "display" or "signatureChain") continue;
            values.TryGetValue(f.Key, out var v);
            if (f.Required == true && IsEmpty(v)) errors.Add(new FieldError(f.Key, f.RequiredMessage ?? $"Συμπληρώστε το πεδίο «{f.Label}»."));
            if (f.Kind == "text" && f.MaxLength is int max && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > max)
                errors.Add(new FieldError(f.Key, $"Έως {max} χαρακτήρες."));
            if (f.Kind == "files" && v.ValueKind == JsonValueKind.Array)
                foreach (var file in v.EnumerateArray())
                {
                    var tempId = file.TryGetProperty("TempId", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    var kept = file.TryGetProperty("FileId", out var k) && k.ValueKind == JsonValueKind.Number;
                    if (!kept && temp.Find(tempId, me) is null) errors.Add(new FieldError(f.Key, "Ένα αρχείο δεν βρέθηκε· ανεβάστε το ξανά."));
                }
        }
        return errors;
    }

    /// <summary>The values posted: only fields of the form, editable, on screen (and of the brief registration).</summary>
    private static Dictionary<string, JsonElement> Sent(RegisterForm form, Dictionary<string, JsonElement> values, bool brief)
    {
        var keys = form.Fields
            .Where(f => f.Kind is not ("display" or "signatureChain") && f.ReadOnly != true && (!brief || f.Brief == true) && !Hidden(f, values))
            .Select(f => f.Key).ToHashSet();
        return values.Where(kv => keys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /* ---------- register, edit ---------- */

    public async Task<RegisterPayload> Register(RegisterRequest req, CancellationToken ct)
    {
        var form = await Form(req.GroupId, req.NewType, req.Old == true, req.From, req.Mode, ct);
        var errors = Validate(form, req.Values, req.Brief);
        if (errors.Count > 0) return new RegisterPayload { Errors = errors };
        var sent = Sent(form, req.Values, req.Brief);
        // An answer is always related to the answered document, as «Απάντηση σε».
        var reply = req.Mode == "answer" ? req.From : null;
        var fields = Fields(form, sent, req.GroupId, req.Old == true && req.From is null, reply);
        var r = await writer.Register(req.GroupId, null, fields, form.DocumentType == DocumentType.Draft, true, ct);
        return await Payload(r, req.GroupId, ct);
    }

    public async Task<RegisterPayload> Edit(int id, EditRequest req, CancellationToken ct)
    {
        var form = await EditForm(id, req.GroupId, ct);
        // Read-only fields keep their value whatever is sent; null clears a field.
        var editable = form.Fields.Where(f => f.ReadOnly != true && f.Kind is not ("display" or "signatureChain")).Select(f => f.Key).ToHashSet();
        var sent = req.Values.Where(kv => editable.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        // The values after the edit: current ones, replaced by what was sent. The Razor form posts
        // every field, so all of them are posted (a missing field would be cleared).
        var after = form.Fields.Where(f => f.Value is not null && f.Kind is not ("display" or "signatureChain"))
            .ToDictionary(f => f.Key, f => JsonSerializer.SerializeToElement(f.Value, Json.Options));
        foreach (var (k, v) in sent) after[k] = v;
        var errors = Validate(form, after, false);
        if (errors.Count > 0) return new RegisterPayload { Errors = errors };
        var fields = Fields(form, after, req.GroupId, false, null);
        // A draft posts its signature chain too: the stored one, or a new calculation when the signers changed.
        var signers = new[] { "FinalSigners", "ExtraCosigners", "CoAuthors", "Ratifiers" };
        var current = form.Fields.Where(f => signers.Contains(f.Key))
            .ToDictionary(f => f.Key, f => JsonSerializer.Serialize(f.Value, Json.Options));
        var changed = signers.Any(k => sent.TryGetValue(k, out var v) &&
            !SamePeople(v, current.GetValueOrDefault(k)));
        var r = await writer.Register(req.GroupId, id, fields, form.DocumentType == DocumentType.Draft, changed, ct);
        return await Payload(r, req.GroupId, ct);
    }

    private static bool SamePeople(JsonElement sent, string? current)
    {
        static string Key(IEnumerable<PersonValue> l) => string.Join(",", l.Select(p => $"{p.UserId}-{p.GroupId}").Order());
        var a = sent.ValueKind == JsonValueKind.Array ? sent.Deserialize<List<PersonValue>>(Json.Options) ?? [] : [];
        var b = current is null ? [] : JsonSerializer.Deserialize<List<PersonValue>>(current, Json.Options) ?? [];
        return Key(a) == Key(b);
    }

    private async Task<RegisterPayload> Payload(ClassicWriter.Outcome r, int groupId, CancellationToken ct)
    {
        if (!r.Success) throw new ApiException(r.Message ?? "Η καταχώριση δεν ολοκληρώθηκε.");
        if (r.DocumentId is not int id) throw new ApiException("Η καταχώριση έγινε, αλλά δεν βρέθηκε το έγγραφο.");
        string? protocol = null;
        try
        {
            protocol = (await view.View(id, groupId, ct)).ProtocolText;
        }
        catch (ApiException)
        {
            // Registered for another group (e.g. sent on): the id is enough.
        }
        return new RegisterPayload { DocumentId = id, ProtocolText = protocol };
    }

    /// <summary>
    /// The fields DocumentPropertiesEditor posts for these values (names as in the HTML), for
    /// DocumentModelBinder. Only keys present in <paramref name="v"/> are written.
    /// </summary>
    internal static RazorFields Fields(RegisterForm form, Dictionary<string, JsonElement> v, int groupId, bool old, int? replyTo)
    {
        const string D = "CurrentDocument.";
        var f = new RazorFields();
        // Hidden inputs of the editor.
        f.Add(D + "Type.Id", form.DocumentType);
        f.Add("AppContext.DocNewTypes.Id", form.NewType);
        f.Add("DocOrgGroupId", groupId);
        f.Add(D + "EditingDocument", true);
        f.Add(D + "ShouldSkipAlreadyRegisteredCheck", false);
        f.Add(D + "MainFile.WasEncrypted", "False");
        f.Add("ShouldRecreateFileFromTemplate", "False");

        string? T(string key) => RazorFields.Text(v, key);
        bool Has(string key) => v.ContainsKey(key);
        void Text(string key, string name)
        {
            if (Has(key)) f.Add(name, T(key) ?? "");
        }
        void Check(string key, string name)
        {
            if (Has(key)) f.Add(name, RazorFields.Bool(v, key));
        }
        void Day(string key, string name)
        {
            if (Has(key)) f.Add(name, RazorFields.Day(T(key)) ?? "");
        }
        void One(string key, string name)
        {
            if (Has(key) && RazorFields.Int(v, key) is int i) f.Add(name, i);
        }
        void Many(string key, string name)
        {
            if (Has(key)) f.AddAll(name, RazorFields.Ints(v, key).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        List<PersonValue> People(string key) => RazorFields.List<PersonValue>(v, key);

        Text("Title", D + "Title");
        if (Has("SaveTitle")) f.Add(D + "ShouldSaveCurrentTitleAsPredefined", RazorFields.Bool(v, "SaveTitle"));
        Text("Summary", D + "Summary");
        Check("Oikothen", D + "IsOikothen");
        Check("FromTemplate", D + "CreateMainFileFromTemplate");
        if (!RazorFields.Bool(v, "FromTemplate")) f.Files(D + "MainFile", RazorFields.List<UploadedFile>(v, "MainFile"));
        f.Files(D + "Attachments", RazorFields.List<UploadedFile>(v, "Attachments"));
        f.Files(D + "InternalAttachments", RazorFields.List<UploadedFile>(v, "InternalAttachments"));
        f.Files(D + "SignerAttachments", RazorFields.List<UploadedFile>(v, "SignerAttachments"));

        if (Has("Related") || replyTo is not null)
        {
            var related = RazorFields.List<RelatedValue>(v, "Related");
            if (replyTo is int r && related.All(x => x.Id != r)) related.Insert(0, new RelatedValue { Id = r });
            foreach (var x in related.DistinctBy(x => x.Id))
            {
                f.Add(D + "Related.Document.Id", x.Id);
                f.Add(D + "Related.Relation.Id", x.Id == replyTo ? Classic.RelationReply : x.RelationId ?? Classic.RelationOther);
            }
        }

        if (Has("KindId") && RazorFields.Int(v, "KindId") is int kind)
        {
            f.Add(D + "Kind.Id", kind);
            f.Add(D + "SelectedKindId", kind);
        }
        if (v.TryGetValue("KindData", out var kd) && kd.ValueKind == JsonValueKind.Object) f.Add(D + "DocumentKindMetadataInstance", kd.GetRawText());
        Check("SensitiveData", D + "ContainsSensitiveData");
        Many("Categories", D + "MainCategories.Id");
        if (Has("ProtocolBook")) f.Add(D + "IdAltProtocol", RazorFields.Int(v, "ProtocolBook") ?? 0);
        if (old)
        {
            f.Add(D + "IsOldProtocol", true);
            f.Add(D + "HasGlobalProtocol", true);
            Text("OldProtocolNumber", D + "GlobalProtocolText");
            Day("OldProtocolDate", D + "GlobalProtocolDate");
        }
        Text("SenderProtocol", D + "ArPrApostolea");
        Day("SendDate", D + "DateApostolis");
        if (RazorFields.List<ContactValue>(v, "Sender").FirstOrDefault() is { } sender) f.Contacts(D + "Apostoleas", [sender]);
        if (Has("SendReceipt")) f.Add("SendApostoleasPrototocollReceipt", RazorFields.Bool(v, "SendReceipt"));
        if (Has("AltNumbering"))
        {
            var altKind = RazorFields.Int(v, "AltNumbering");
            f.Add(D + "EnableAltNumbering", altKind is not null);
            if (altKind is not null) f.Add(D + "IdAltNumberKind", altKind);
        }

        f.AddAll(D + "Recipients.Id", People("Recipients").Select(RazorFields.UserInGroup));
        f.Contacts(D + "ContactRecipients", RazorFields.List<ContactValue>(v, "OtherRecipients"));
        f.AddAll(D + "Koinopoihseis.Id", People("Notify").Select(RazorFields.UserInGroup));
        f.Contacts(D + "ContactKoinopoihseis", RazorFields.List<ContactValue>(v, "OtherNotify"));
        f.AddAll(D + "InternalDistribution.Id", People("InternalDistribution").Select(RazorFields.UserInGroup));
        f.Contacts(form.DocumentType == DocumentType.Incoming ? D + "ContactTransactorsIncoming" : D + "ContactTransactors",
            RazorFields.List<ContactValue>(v, "Transactors"));

        // Signatures picked by the user; ClassicWriter adds the calculated chain of a draft.
        f.AddAll(D + "Signatures.ToSign.Id", People("FinalSigners").Select(p => p.GroupId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Text("ExternalSigners", D + "ExternalSignatures");
        f.AddAll(D + "RatifierSignatures.ToSign.Id", People("Ratifiers").Select(p => p.GroupId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        f.AddAll(D + "Cosignatures.ToSign.Id", People("ExtraCosigners").Select(p => p.GroupId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        f.AddAll(D + "CoAuthorsWithSignature.Signator.Id", People("CoAuthors").Where(p => p.UserId is not null).Select(RazorFields.UserInGroup));

        Many("SendTypes", D + "SendTypes.Id");
        Text("IssuePlace", D + "IssuePlace");
        if (People("Author").FirstOrDefault(p => p.UserId is not null) is { } author) f.Add(D + "Author.Id", author.UserId);
        Text("Comments", D + "Comments");
        Text("Endiaferomenos", D + "Endiaferomenos");
        One("Priority", D + "Priority.Id");
        One("Classification", D + "Diavathmisi.Id");
        Check("IsPersonal", D + "IsPersonal");
        Day("KeepUntil", D + "KeepUntil");
        One("RetentionInMonths", D + "RetentionInMonths");
        f.Folders(RazorFields.List<ThematicFolder>(v, "ThematicFolders"));
        if (People("CreatedForGroup").FirstOrDefault() is { } forGroup) f.Add(D + "CreatedForGroup.Id", forGroup.GroupId);
        if (RazorFields.List<ContactValue>(v, "OnBehalfOf").FirstOrDefault() is { } behalf) f.Contacts(D + "OnBehalfOf", [behalf]);
        return f;
    }

    private sealed class GroupRow
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public bool HasManager { get; set; }
    }

    /// <summary>The diagram inputs ("{group}&amp;Calculated" or "{user}-{group}&amp;Calculated") as steps.</summary>
    internal static List<StepRow> ParseChain(IEnumerable<(string Key, string Value)> inputs)
    {
        var rows = new List<StepRow>();
        foreach (var (key, value) in inputs)
        {
            var type = key switch
            {
                "CurrentDocument.CoAuthorsWithSignature.Signator.Id" => Classic.SignatureAuthor,
                "CurrentDocument.Cosignatures.ToSign.Id" => Classic.SignatureEndorsement,
                "CurrentDocument.RatifierSignatures.ToSign.Id" => Classic.SignatureRatifier,
                _ => Classic.SignatureFinal,
            };
            var ids = value.Split('&')[0].Split('-');
            if (ids.Length == 2 && int.TryParse(ids[0], out var u) && int.TryParse(ids[1], out var g))
                rows.Add(new StepRow { Type = type, UserId = u, GroupId = g });
            else if (ids.Length == 1 && int.TryParse(ids[0], out var only))
                rows.Add(new StepRow { Type = type, GroupId = only });
        }
        return rows;
    }

    internal sealed class StepRow
    {
        public int Type { get; set; }
        public int GroupId { get; set; }
        public string? Group { get; set; }
        public int? UserId { get; set; }
        public string? User { get; set; }
        public bool HasManager { get; set; }
    }

    /// <summary>
    /// The signatures a draft will get, for the preview: what «Υπολογισμός υπογραφών» of the editor
    /// returns (_Helpers/EndorsementCalculation: ApplicationContext.CalculateSignatures).
    /// </summary>
    public async Task<SignatureChainPayload> SignatureChain(SignatureChainRequest req, CancellationToken ct)
    {
        var form = new RegisterForm { NewType = 6, DocumentType = DocumentType.Draft };
        var values = new Dictionary<string, JsonElement>
        {
            ["FinalSigners"] = JsonSerializer.SerializeToElement(req.Finals, Json.Options),
            ["ExtraCosigners"] = JsonSerializer.SerializeToElement(req.Extra ?? [], Json.Options),
            ["CoAuthors"] = JsonSerializer.SerializeToElement(req.CoAuthors ?? [], Json.Options),
            ["Ratifiers"] = JsonSerializer.SerializeToElement(req.Ratifiers ?? [], Json.Options),
        };
        if (req.Finals.Count == 0) return new SignatureChainPayload();
        var fields = Fields(form, values, req.GroupId, false, null);
        var inputs = await writer.Chain(req.GroupId, null, fields.Fields, true, null, ct);
        var rows = ParseChain(inputs);
        var groupIds = rows.Select(r => r.GroupId).Distinct().ToList();
        var userIds = rows.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).Distinct().ToList();
        var groups = groupIds.Count == 0 ? [] : (await db.Query<GroupRow>(
            "SELECT IdOrgGroup AS Id, Name, CASE WHEN IdActiveManager IS NULL THEN 0 ELSE 1 END AS HasManager FROM Organization.OrgGroup WHERE IdOrgGroup IN @groupIds",
            new { groupIds }, ct)).ToDictionary(g => g.Id);
        var users = userIds.Count == 0 ? [] : (await db.Query<GroupRow>(
            "SELECT IdOrgUser AS Id, NULLIF(LTRIM(RTRIM(ISNULL(Surname, '') + ' ' + ISNULL(Name, ''))), '') AS Name FROM Organization.OrgUser WHERE IdOrgUser IN @userIds",
            new { userIds }, ct)).ToDictionary(u => u.Id);
        foreach (var r in rows)
        {
            if (groups.TryGetValue(r.GroupId, out var g)) { r.Group = g.Name; r.HasManager = g.HasManager; }
            if (r.UserId is int u && users.TryGetValue(u, out var person)) r.User = person.Name;
        }

        bool Picked(StepRow s) => s.Type switch
        {
            Classic.SignatureFinal => req.Finals.Any(x => x.GroupId == s.GroupId),
            Classic.SignatureEndorsement => (req.Extra ?? []).Any(x => x.GroupId == s.GroupId),
            Classic.SignatureRatifier => (req.Ratifiers ?? []).Any(x => x.GroupId == s.GroupId),
            _ => (req.CoAuthors ?? []).Any(x => x.GroupId == s.GroupId && x.UserId == s.UserId),
        };
        string Kind(int type) => type switch
        {
            Classic.SignatureAuthor => "Υπογραφή συντάκτη",
            Classic.SignatureEndorsement => "Προσυπογραφή",
            Classic.SignatureRatifier => "Υπογραφή εγκρίνοντος",
            _ => "Τελική υπογραφή",
        };
        // In the order of the diagram: author and co-authors, endorsements, ratifier, final signatures.
        var order = new[] { Classic.SignatureAuthor, Classic.SignatureEndorsement, Classic.SignatureRatifier, Classic.SignatureFinal };
        var steps = order.SelectMany(t => rows.Where(r => r.Type == t)).ToList();
        return new SignatureChainPayload
        {
            Steps = steps.Select((s, i) => new SignatureStep { Order = i + 1, Kind = Kind(s.Type), Group = s.Group ?? "", User = s.User, Auto = !Picked(s) }).ToList(),
            Warnings = steps.Where(s => s.UserId is null && !s.HasManager)
                .Select(s => $"Η υπηρεσία «{s.Group}» δεν έχει ενεργό προϊστάμενο.").Distinct().ToList() is { Count: > 0 } w ? w : null,
        };
    }

    public Task<UploadedFile> Upload(IFormFile file, CancellationToken ct) => temp.Save(file, user.Required.Id, ct);
}
