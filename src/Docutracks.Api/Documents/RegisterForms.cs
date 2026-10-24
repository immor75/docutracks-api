using System.Text.Json.Nodes;

namespace Docutracks.Api.Documents;

/// <summary>
/// The forms of «Νέο έγγραφο» (DocumentPropertiesEditor) for Εισερχόμενο (1), Σχέδιο (6),
/// Εξερχόμενο (7) and Υποστηρικτικά (5), with the options of the installation (SQL) and the
/// organisation (v1). The same descriptions as the mock (docs/documents-api.md, phase 8).
/// </summary>
public sealed class RegisterForms(IDb db, IReference reference, ISettings settings, ActionsService actions, UserContext user)
{
    /// <summary>NewDocTypes → DocumentType.</summary>
    public static readonly Dictionary<int, int> DocumentTypeOf = new() { [1] = 1, [7] = 2, [6] = 3, [5] = 4 };

    public static readonly Dictionary<int, int> NewTypeOf = new() { [1] = 1, [2] = 7, [3] = 6, [4] = 5 };

    /// <summary>Groups whose kind cannot sign (Common.Constants.GroupKind.DIRECTORY, CENTRAL_PROTOCOL).</summary>
    private const int Directory = 6;

    private static readonly List<Tab> AllTabs =
    [
        new("basic", "Βασικά"), new("kind", "Είδος"), new("publications", "Δημοσιεύσεις"), new("categories", "Θεματικές Κατηγορίες"),
        new("protocol", "Πρωτόκολλο"), new("distribution", "Διανομή"), new("signatures", "Υπογραφές"), new("extras", "Λοιπά"),
    ];

    private sealed class TitleRow
    {
        public string? Title { get; set; }
    }

    public async Task<RegisterForm> Build(int groupId, int newType, bool old, CancellationToken ct)
    {
        if (!DocumentTypeOf.TryGetValue(newType, out var docType)) throw new ApiException("Η καταχώριση αυτού του τύπου γίνεται ακόμη στο κλασικό UI.");
        var fields = docType switch
        {
            DocumentType.Incoming => await Incoming(groupId, old, ct),
            DocumentType.Draft => await Draft(groupId, ct),
            DocumentType.Outgoing => await Outgoing(groupId, old, ct),
            _ => await Supporting(groupId, old, ct),
        };
        var used = fields.Select(f => f.Tab).ToHashSet();
        var toggle = await settings.Bool("ShowDetailedOrBriefDocumentCreationToggle", true, ct);
        var label = newType switch { 1 => "Εισερχόμενο", 6 => "Σχέδιο", 7 => "Εξερχόμενο", _ => "Υποστηρικτικά Έγγραφα" };
        return new RegisterForm
        {
            NewType = newType,
            DocumentType = docType,
            Title = old ? $"Καταχώριση Παλαιότερου Εγγράφου ({label})" : $"Καταχώριση Νέου Εγγράφου ({label})",
            Tabs = AllTabs.Where(t => used.Contains(t.Key)).ToList(),
            // Drafts have no brief registration in the Razor editor.
            AllowBrief = toggle && docType != DocumentType.Draft,
            BriefDefault = docType switch
            {
                DocumentType.Incoming => await settings.Bool("IsBriefIncomingDefault", false, ct),
                DocumentType.Outgoing => await settings.Bool("IsBriefOutgoingDefault", false, ct),
                _ => false,
            },
            Fields = fields,
        };
    }

    /* ---------- common pieces ---------- */

    private async Task<List<string>> PredefinedTitles(int groupId, int docType, CancellationToken ct) =>
        (await db.Query<TitleRow>(
            """
            SELECT DISTINCT pt.Title FROM Document.PredefinedTitle pt
             WHERE pt.IsCentral = 1
                OR EXISTS (SELECT 1 FROM Document.PredefinedTitleOrgGroupDocumentType x
                            WHERE x.IdPredefinedTitle = pt.IdPredefinedTitle AND x.IdOrgGroup = @groupId AND (x.IdDocumentType = @docType OR x.IdDocumentType IS NULL))
             ORDER BY pt.Title
            """, new { groupId, docType }, ct)).Select(r => r.Title).OfType<string>().ToList();

    private async Task<List<Option>> ProtocolBooks(int groupId, CancellationToken ct)
    {
        var alt = await db.Query<IdTitle>(
            """
            SELECT ap.IdAltProtocol AS Id, ap.AltProtocolName AS Title FROM Document.AltProtocol ap
             INNER JOIN Document.AltProtocolOrgGroup apg ON apg.IdAltProtocol = ap.IdAltProtocol
             WHERE apg.IdOrgGroup = @groupId AND ap.IsActive = 1 ORDER BY ap.AltProtocolName
            """, new { groupId }, ct);
        return [new Option(0, "Βασικό Πρωτόκολλο"), .. alt.Select(a => new Option(a.Id, a.Title ?? $"Βιβλίο {a.Id}"))];
    }

    private async Task<List<Option>> AltNumberKinds(int groupId, CancellationToken ct) =>
        (await db.Query<IdTitle>(
            $"""
            SELECT k.IdAltNumberKind AS Id, {Sql.Title("[Document].[AltNumberKindTitleResources]", "k.IdAltNumberKind")} AS Title
              FROM Document.AltNumberKind k
             WHERE k.IsActive = 1 AND (k.ValidUntil IS NULL OR k.ValidUntil > GETDATE())
               AND (NOT EXISTS (SELECT 1 FROM Document.AltNumberKindOwnerGroup o WHERE o.IdAltNumberKind = k.IdAltNumberKind)
                    OR EXISTS (SELECT 1 FROM Document.AltNumberKindOwnerGroup o WHERE o.IdAltNumberKind = k.IdAltNumberKind AND o.IdOrgGroup = @groupId))
            """, new { groupId }, ct)).Select(r => new Option(r.Id, r.Title ?? r.Id.ToString())).ToList();

    private sealed class IdTitle
    {
        public int Id { get; set; }
        public string? Title { get; set; }
    }

    private async Task<List<Option>> Active(string table, string id, string resources, CancellationToken ct) =>
        (await reference.Titles(table, id, resources, ct, "t.IsActive = 1 AND (t.ValidUntil IS NULL OR t.ValidUntil > GETDATE())")).ToList();

    private async Task<List<TreeOption>> CategoriesTree(CancellationToken ct) =>
        Trees.Build(await reference.Categories(ct), c => c.IdCategory, c => c.IdParentCategory, c => c.Title ?? c.IdCategory.ToString());

    /// <summary>Groups that can be final signers (nonSelectables of the view): sign documents, not the protocol, not directories.</summary>
    private async Task<List<int>> SigningGroups(int groupId, CancellationToken ct) =>
        (await db.Query<IdTitle>(
            "SELECT IdOrgGroup AS Id FROM Organization.OrgGroup WHERE IsActive = 1 AND (SignsDocuments = 1 OR IdOrgGroup = @groupId) AND ISNULL(IdOrgGroupKind, 0) NOT IN (1, @dir)",
            new { groupId, dir = Directory }, ct)).Select(r => r.Id).ToList();

    private async Task<(JsonNodeValue Full, JsonNodeValue Groups)> Org(CancellationToken ct)
    {
        var root = await db.Scalar<int?>("SELECT TOP 1 IdOrgGroup FROM Organization.OrgGroup WHERE IdParentOrgGroup IS NULL AND IsActive = 1 ORDER BY IdOrgGroup", ct: ct) ?? 1;
        var tree = await actions.OrgTree(root, ct);
        var groups = tree.DeepClone();
        void Strip(JsonNode? n)
        {
            if (n is not JsonObject o) return;
            o["Users"] = new JsonArray();
            foreach (var c in o["SubGroups"]?.AsArray() ?? []) Strip(c);
        }
        foreach (var n in groups.AsArray()) Strip(n);
        return (new JsonNodeValue(tree), new JsonNodeValue(groups));
    }

    private static RegisterField F(string key, string kind, string label, string tab) => new() { Key = key, Kind = kind, Label = label, Tab = tab };

    private const string Required = "Το πεδίο αυτό είναι υποχρεωτικό";

    private async Task<List<RegisterField>> Basic(int groupId, int docType, CancellationToken ct) =>
    [
        new() { Key = "Title", Kind = "text", Label = "Θέμα Εγγράφου", Tab = "basic", Brief = true, Required = true, RequiredMessage = Required,
            Multiline = true, MaxLength = 2000, Suggestions = await PredefinedTitles(groupId, docType, ct) },
        F("SaveTitle", "checkbox", "Αποθήκευση ως προκαθορισμένο θέμα (για επαναχρησιμοποίηση σε επόμενες καταχωρίσεις)", "basic"),
    ];

    private async Task<int> MaxSize(CancellationToken ct) => await settings.Int("MaxUploadFileSizeInMb", 50, ct);

    private RegisterField Files(string key, string label, string role, bool multi, int max, bool brief = false) =>
        new() { Key = key, Kind = "files", Label = label, Tab = "basic", Role = role, Multi = multi, MaxSizeMb = max, Brief = brief ? true : null };

    /* ---------- the four forms ---------- */

    private async Task<List<RegisterField>> Incoming(int groupId, bool old, CancellationToken ct)
    {
        var max = await MaxSize(ct);
        var (org, groups) = await Org(ct);
        var f = new List<RegisterField>();
        f.AddRange(await Basic(groupId, DocumentType.Incoming, ct));
        f.Add(new() { Key = "Summary", Kind = "text", Label = "Περίληψη", Tab = "basic", Multiline = true, MaxLength = 4000 });
        f.Add(Files("MainFile", "Κυρίως έγγραφο σε ψηφιακή μορφή", "main", false, max, brief: true));
        f.Add(new() { Key = "Related", Kind = "documents", Label = "Σχετικά Έγγραφα", Tab = "basic", Brief = true });
        f.Add(Files("Attachments", "Συνημμένα Αρχεία", "attachment", true, max, brief: true));
        f.Add(Files("InternalAttachments", "Συνημμένα Εσωτερικής Χρήσης", "internal", true, max));
        f.AddRange(await KindFields(ct));
        f.Add(F("SensitiveData", "checkbox", "Προσωπικά Δεδομένα για Διαύγεια", "publications"));
        f.Add(new() { Key = "Categories", Kind = "tree", Source = "options", Label = "Κατηγορίες Φορέα", Tab = "categories", Multi = true, Tree = await CategoriesTree(ct) });
        f.Add(new() { Key = "ProtocolBook", Kind = "select", Label = "Βιβλία Πρωτοκόλλησης", Tab = "protocol", Brief = true, Options = await ProtocolBooks(groupId, ct), Value = 0 });
        if (old) f.AddRange(OldProtocol());
        f.Add(new() { Key = "SenderProtocol", Kind = "text", Label = "Α. Π. Αποστολέα", Tab = "protocol", Brief = true, MaxLength = 100 });
        f.Add(new() { Key = "SendDate", Kind = "date", Label = "Ημ/νία Αποστολής", Tab = "protocol", Brief = true });
        f.Add(new() { Key = "Sender", Kind = "contacts", Label = "Αποστολέας", Tab = "protocol", Brief = true, Required = true, RequiredMessage = "Επιλέξτε ή γράψτε αποστολέα.", Multi = false, AllowNew = true });
        f.Add(new() { Key = "SendReceipt", Kind = "checkbox", Label = "Αποστολή Αποδεικτικού Πρωτοκόλλησης", Tab = "protocol", Hint = "Στο e-mail του αποστολέα, μετά την πρωτοκόλληση." });
        f.AddRange(await AltNumbering(groupId, ct));
        f.AddRange(Distribution(org, groups, internalDistribution: false));
        f.AddRange(await Extras(DocumentType.Incoming, groupId, groups, ct));
        return f;
    }

    private async Task<List<RegisterField>> Draft(int groupId, CancellationToken ct)
    {
        var max = await MaxSize(ct);
        var (org, groups) = await Org(ct);
        var f = new List<RegisterField>();
        f.AddRange(await Basic(groupId, DocumentType.Draft, ct));
        f.Add(F("Oikothen", "checkbox", "Οίκοθεν", "basic"));
        f.Add(new() { Key = "Summary", Kind = "text", Label = "Περίληψη", Tab = "basic", Multiline = true, MaxLength = 4000 });
        f.Add(new() { Key = "FromTemplate", Kind = "checkbox", Label = "Από Πρότυπο", Tab = "basic", Hint = "Το κυρίως έγγραφο δημιουργείται από το πρότυπο του είδους." });
        var main = Files("MainFile", "Κυρίως έγγραφο σε ψηφιακή μορφή", "main", false, max, brief: true);
        main.Required = true;
        main.RequiredMessage = "Επιλέξτε το κυρίως έγγραφο ή «Από Πρότυπο».";
        main.HiddenIf = new HiddenIf("FromTemplate", true);
        f.Add(main);
        f.Add(new() { Key = "Related", Kind = "documents", Label = "Σχετικά Έγγραφα", Tab = "basic", Brief = true });
        f.Add(Files("Attachments", "Συνημμένα Αρχεία", "attachment", true, max, brief: true));
        f.Add(Files("InternalAttachments", "Συνημμένα Εσωτερικής Χρήσης", "internal", true, max));
        var signer = Files("SignerAttachments", "Συνημμένα Υπογράφοντων", "signer", true, max);
        signer.Hint = "Συνημμένα στα οποία έχουν πρόσβαση αποκλειστικά οι υπογράφοντες και ο συντάκτης";
        f.Add(signer);
        f.AddRange(await KindFields(ct));
        f.Add(new() { Key = "Categories", Kind = "tree", Source = "options", Label = "Κατηγορίες Φορέα", Tab = "categories", Multi = true, Tree = await CategoriesTree(ct) });
        f.Add(new() { Key = "ProtocolBook", Kind = "select", Label = "Βιβλία Πρωτοκόλλησης", Tab = "protocol", Brief = true, Options = await ProtocolBooks(groupId, ct), Value = 0 });
        f.AddRange(await AltNumbering(groupId, ct));
        f.AddRange(Distribution(org, groups, internalDistribution: true));
        f.AddRange(await Signatures(groupId, org, groups, chain: true, ct));
        f.AddRange(await Extras(DocumentType.Draft, groupId, groups, ct));
        return f;
    }

    private async Task<List<RegisterField>> Outgoing(int groupId, bool old, CancellationToken ct)
    {
        var draft = await Draft(groupId, ct);
        var brief = new HashSet<string> { "Title", "MainFile", "Related", "Attachments", "ProtocolBook", "OldProtocolNumber", "OldProtocolDate", "Recipients", "OtherRecipients", "FinalSigners" };
        var f = new List<RegisterField>();
        foreach (var x in draft)
        {
            if (x.Key is "Chain" or "Ratifiers") continue;
            if (x.Key == "AltNumbering" && old) f.AddRange(OldProtocol());
            if (x.Key == "Priority") f.Add(new() { Key = "OnBehalfOf", Kind = "contacts", Label = "Εκ μέρους", Tab = "extras", Multi = false, AllowNew = false });
            x.Brief = brief.Contains(x.Key) ? true : null;
            f.Add(x);
        }
        return f;
    }

    private async Task<List<RegisterField>> Supporting(int groupId, bool old, CancellationToken ct)
    {
        var drop = new HashSet<string> { "Oikothen", "FromTemplate", "OnBehalfOf", "CreatedForGroup" };
        var f = (await Outgoing(groupId, old, ct)).Where(x => !drop.Contains(x.Key)).ToList();
        foreach (var x in f.Where(x => x.Key == "MainFile"))
        {
            x.Required = null;
            x.RequiredMessage = null;
            x.HiddenIf = null;
        }
        return f;
    }

    private static IEnumerable<RegisterField> OldProtocol() =>
    [
        new() { Key = "OldProtocolNumber", Kind = "text", Label = "Αριθμ. Πρωτ.", Tab = "protocol", Brief = true, Required = true, RequiredMessage = Required, MaxLength = 30 },
        new() { Key = "OldProtocolDate", Kind = "date", Label = "Ημ. Πρωτοκόλλου", Tab = "protocol", Brief = true, Required = true, RequiredMessage = Required },
    ];

    private async Task<IEnumerable<RegisterField>> KindFields(CancellationToken ct)
    {
        var defaultKind = await settings.Int("DefaultDocKindId", -1, ct);
        return
        [
            new() { Key = "KindId", Kind = "tree", Source = "kinds", Label = "Είδος Εγγράφου", Tab = "kind", Required = true, RequiredMessage = "Επιλέξτε είδος εγγράφου.",
                Multi = false, Value = defaultKind >= 0 ? defaultKind : null },
            new() { Key = "KindData", Kind = "kindForm", Label = "Ειδικά Πεδία", Tab = "kind", KindField = "KindId" },
        ];
    }

    private async Task<IEnumerable<RegisterField>> AltNumbering(int groupId, CancellationToken ct)
    {
        var kinds = await AltNumberKinds(groupId, ct);
        return kinds.Count == 0
            ? []
            : [new() { Key = "AltNumbering", Kind = "select", Label = "Τύπος Ανεξάρτητης Αρίθμησης", Tab = "protocol", Options = kinds, Hint = "Κενό: χωρίς ανεξάρτητη αρίθμηση." }];
    }

    private static IEnumerable<RegisterField> Distribution(JsonNodeValue org, JsonNodeValue groups, bool internalDistribution)
    {
        var list = new List<RegisterField>
        {
            new() { Key = "Recipients", Kind = "people", Label = "Εσωτερικοί", Section = "Αποδέκτες", Tab = "distribution", Brief = true, Pick = "both", Multi = true, Tree = org },
            new() { Key = "OtherRecipients", Kind = "contacts", Label = "Λοιποί Αποδέκτες", Section = "Αποδέκτες", Tab = "distribution", Multi = true, AllowNew = true },
            new() { Key = "Notify", Kind = "people", Label = "Εσωτερικοί", Section = "Κοινοποιήσεις", Tab = "distribution", Pick = "groups", Multi = true, Tree = groups },
            new() { Key = "OtherNotify", Kind = "contacts", Label = "Λοιπές Κοινοποιήσεις", Section = "Κοινοποιήσεις", Tab = "distribution", Multi = true, AllowNew = true },
        };
        if (internalDistribution)
            list.Add(new() { Key = "InternalDistribution", Kind = "people", Label = "Εσωτερική Διανομή", Section = "Εσωτερική Διανομή", Tab = "distribution", Pick = "both", Multi = true, Tree = org });
        list.Add(new() { Key = "Transactors", Kind = "contacts", Label = "Συναλλασσόμενοι", Section = "Συναλλασσόμενοι", Tab = "distribution", Multi = true, AllowNew = false });
        return list;
    }

    private async Task<IEnumerable<RegisterField>> Signatures(int groupId, JsonNodeValue org, JsonNodeValue groups, bool chain, CancellationToken ct)
    {
        var allowed = await SigningGroups(groupId, ct);
        var list = new List<RegisterField>
        {
            new()
            {
                Key = "FinalSigners", Kind = "people", Label = "Τελικοί Υπογράφοντες (Εσωτερικοί)", Section = "Υπογραφές", Tab = "signatures", Pick = "groups", Multi = true,
                Tree = groups, AllowedGroups = allowed, Required = await settings.Bool("FinalSignatorRequired", true, ct) ? true : null,
                RequiredMessage = "Δεν έχει επιλεγεί τελικός υπογράφων.",
                Hint = "Επιλέξετε αποκλειστικά τον τελικό υπογράφοντα (ή τους τελικούς υπογράφοντες), χωρίς τις ενδιάμεσες προσυπογραφές (οι οποίες υπολογίζονται αυτόματα).",
            },
        };
        if (chain) list.Add(new() { Key = "Chain", Kind = "signatureChain", Label = "Σειρά υπογραφών", Tab = "signatures", From = new ChainFrom("FinalSigners", "ExtraCosigners", "CoAuthors", "Ratifiers") });
        list.Add(new() { Key = "ExternalSigners", Kind = "text", Label = "Τελικοί Υπογράφοντες (Εξωτερικοί)", Tab = "signatures", Multiline = true, MaxLength = 1000 });
        if (chain && await settings.Bool("EnableRatifierSignee", false, ct))
            list.Add(new() { Key = "Ratifiers", Kind = "people", Label = "Υπογραφή Εγκρίνοντος", Tab = "signatures", Pick = "groups", Multi = false, Tree = groups, AllowedGroups = allowed,
                Hint = "Αυτόματος υπολογισμός βάσει των τελικών υπογραφόντων." });
        list.Add(new() { Key = "ExtraCosigners", Kind = "people", Label = "Προσυπογραφές", Tab = "signatures", Pick = "groups", Multi = true, Tree = groups, AllowedGroups = allowed,
            Hint = "Επιλέξετε αποκλειστικά τους προσυπογράφοντες που δεν έχουν υπολογιστεί αυτόματα." });
        list.Add(new() { Key = "CoAuthors", Kind = "people", Label = "Υπογραφές συντάκτη/συνεισηγητών", Tab = "signatures", Pick = "users", Multi = true, Tree = org,
            Hint = "Επιλέξετε αποκλειστικά τα πρόσωπα που δεν έχουν υπολογιστεί αυτόματα." });
        return list;
    }

    private async Task<IEnumerable<RegisterField>> Extras(int docType, int groupId, JsonNodeValue groups, CancellationToken ct)
    {
        var me = user.Required;
        var list = new List<RegisterField>
        {
            new() { Key = "SendTypes", Kind = "select", Label = "Τρόποι Αποστολής", Tab = "extras", Multi = true,
                Options = await Active("Document.SendType", "IdSendType", "[Document].[SendTypeTitleResources]", ct), Value = new List<int>() },
        };
        if (docType != DocumentType.Incoming)
        {
            list.Add(new() { Key = "IssuePlace", Kind = "text", Label = "Τόπος Έκδοσης", Tab = "extras", MaxLength = 200 });
            var (org, _) = await Org(ct);
            list.Add(new() { Key = "Author", Kind = "people", Label = "Συντάκτης", Tab = "extras", Pick = "users", Multi = false, Tree = org,
                Value = new List<PersonValue> { new() { UserId = me.Id, GroupId = groupId } } });
        }
        list.Add(new() { Key = "Comments", Kind = "text", Label = "Σχόλια", Tab = "extras", Multiline = true, MaxLength = 1000 });
        list.Add(new() { Key = "Endiaferomenos", Kind = "text", Label = "Ενδιαφερόμενος", Tab = "extras", MaxLength = 200 });
        list.Add(new() { Key = "Priority", Kind = "select", Label = "Προτεραιότητα", Tab = "extras",
            Options = await Active("Document.Priority", "IdPriority", "[Document].[PriorityTitleResources]", ct), Value = 1 });
        list.Add(new() { Key = "Classification", Kind = "select", Label = "Διαβάθμιση", Tab = "extras",
            Options = await Active("Document.Diavathmisi", "IdDiavathmisi", "[Document].[DiavathmisiTitleResources]", ct), Value = 1 });
        list.Add(F("IsPersonal", "checkbox", "Προσωπικά Δεδομένα", "extras"));
        list.Add(F("KeepUntil", "date", "Προθεσμία Ενέργειας", "extras"));
        list.Add(new() { Key = "RetentionInMonths", Kind = "number", Label = "Μέγιστη Περίοδος Διατήρησης (σε μήνες)", Tab = "extras", Min = 0 });
        list.Add(new() { Key = "ThematicFolders", Kind = "thematicFolders", Label = "Θεματικοί Φάκελοι", Tab = "extras",
            AllowNew = await settings.Bool("AllowNewThematicalFolderByUsers", true, ct), Value = new List<ThematicFolder>() });
        list.Add(new() { Key = "IssueDate", Kind = "display", Label = "Ημ.Καταχώρισης", Tab = "extras", Value = "Κατά την καταχώριση" });
        list.Add(new() { Key = "CreatedBy", Kind = "display", Label = "Καταχωριστής", Tab = "extras", Value = me.FullName });
        if (docType != DocumentType.Incoming)
        {
            var defaultGroup = await settings.Int("DefaultCreatedForGroup", 0, ct);
            list.Add(new()
            {
                Key = "CreatedForGroup", Kind = "people", Label = "Εκδούσα Υπηρεσιακή Μονάδα", Tab = "extras", Pick = "groups", Multi = false, Required = true,
                RequiredMessage = "Επιλέξτε εκδούσα υπηρεσιακή μονάδα.", Tree = groups,
                Value = new List<PersonValue> { new() { GroupId = defaultGroup > 0 ? defaultGroup : groupId } },
            });
        }
        return list;
    }
}
