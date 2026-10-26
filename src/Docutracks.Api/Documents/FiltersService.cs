using Microsoft.Extensions.Caching.Memory;

namespace Docutracks.Api.Documents;

/// <summary>`GET filters`: the sidebar, search types, columns, periods and «Φίλτρα» of the list.</summary>
public sealed class FiltersService(IReference reference, ISettings settings, IDb db, UserContext user, IMemoryCache cache)
{
    public static readonly List<Option> Periods =
    [
        new(1, "Τρέχουσα ημέρα"), new(2, "Τελευταία εβδομάδα"), new(3, "Τελευταίος μήνας"), new(4, "Τελευταίο έτος"),
        new(5, "Τρέχον έτος"), new(6, "Όλα"), new(0, "Προσαρμοσμένο"),
    ];

    /// <summary>DateRanges.DEFAULT_DATE_RANGE.</summary>
    public const int DefaultPeriod = 4;

    /// <summary>The columns of List/Index.cshtml, keys as in HideDocumentCatalogColumns.</summary>
    private static readonly (string Key, string Label, int Width)[] AllColumns =
    [
        ("SerialNumber", "Α/Α", 75), ("IssueDate", "Ημ. Δημ/ας", 93), ("LastSignDate", "Ημ. Τελευταίας Μεταβολής", 130),
        ("ProtocolNum", "Αριθμ. Πρωτ.", 145), ("ProtocolDate", "Ημ/νία Πρωτοκόλλου", 110), ("DocumentIdentifier", "Αναγνωριστικό", 100),
        ("Subject", "Θέμα Εγγράφου", 270), ("ArPrApostolea", "Α. Π. Αποστολέα", 132), ("Apostoleas", "Αποστολέας", 260),
        ("EureiasXrisisKind", "Υποείδος", 140), ("SignaturesStatus", "Στάδιο Υπογραφών", 160), ("Recipients", "Αποδέκτες", 208),
        ("CopyStatus", "Κατάσταση", 130), ("NextEntityToSign", "Κάτοχος", 160), ("DocumentKind", "Είδος Εγγράφου", 132),
        ("Assignees", "Χρέωση", 95), ("Folder", "Φάκελοι", 95), ("Endiaferomenos", "Ενδιαφερόμενος", 140), ("Register", "Καταχωριστής", 128),
    ];

    public async Task<FolderCatalog.Context> Context(int groupId, CancellationToken ct)
    {
        var u = user.Required;
        var role = await reference.Role(u.Id, groupId, ct);
        var extra = await cache.GetOrCreateAsync($"listctx:{groupId}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var sub = await db.Scalar<int>("SELECT COUNT(1) FROM Organization.OrgGroup WHERE IdParentOrgGroup = @groupId AND IsActive = 1", new { groupId }, ct);
            var consultants = await db.Scalar<int>(
                "SELECT COUNT(1) FROM Organization.UserRoleGroupRelationship ur WHERE ur.IdOrgGroup = @groupId AND ur.IdOrgRole = @role AND EXISTS (SELECT 1 FROM Organization.OrgRole r WHERE r.IdOrgRole = ur.IdOrgRole AND r.IsAssignable = 1)",
                new { groupId, role = Roles.Consultor }, ct);
            return (HasSubGroups: sub > 0, HasConsultant: consultants > 0);
        });
        return new FolderCatalog.Context(
            role,
            IsForAnswerEnabled: await settings.Bool("IsForAnswerEnabled", false, ct),
            ShowSignaturesStatus: await settings.Bool("ShowSignaturesStatus", false, ct),
            DisplayOutgoingReturned: await settings.Bool("DisplayOutgoingReturned", true, ct),
            ShowOpenDocuments: await settings.Bool("ShowOpenDocuments", false, ct),
            ShowRequestsFilter: await settings.Bool("ShowRequestsFilter", false, ct),
            HasSubGroups: extra.HasSubGroups,
            GroupHasActiveConsultant: extra.HasConsultant);
    }

    public async Task<DocumentFiltersPayload> Get(int groupId, CancellationToken ct)
    {
        var ctx = await Context(groupId, ct);
        var role = ctx.Role;

        var columns = await Columns(ct);

        var searchTypes = new List<Option> { new(1, "Αριθμ. Πρωτ.") };
        if (await settings.Bool("UseDepartmentProtocolSearch", false, ct)) searchTypes.Add(new(10, "Αριθμ. Πρωτ. Τμήματος"));
        searchTypes.AddRange([new(2, "Α. Π. Αποστολέα"), new(3, "Αποστολέας"), new(4, "Θέμα Εγγράφου")]);
        if (await settings.Bool("ShouldUseFilestream", true, ct)) searchTypes.Add(new(5, "Κείμενο Ψηφιακού Αρχείου"));
        searchTypes.AddRange([new(6, "Εξωτερικοί Αποδέκτες"), new(7, "Αναγνωριστικό(Α/Α)"), new(8, "Ανεξάρτητη Αρίθμηση"), new(9, "Ελεύθερο Κείμενο")]);
        if (await settings.Bool("ShowTransactorContacts", false, ct)) searchTypes.Add(new(11, "Συναλλασσόμενοι"));

        var folders = FolderCatalog.Build(ctx);
        return new DocumentFiltersPayload
        {
            Folders = folders,
            DefaultFolderKey = FolderCatalog.DefaultKey(ctx),
            SearchTypes = searchTypes,
            DefaultSearchType = 1,
            Columns = columns,
            Periods = Periods,
            DefaultPeriod = DefaultPeriod,
            DefaultSort = new DocumentSort("IssueDate", "desc"),
            Filters = await FilterDefs(role, ct),
            CanExportWithFiles = role.IsAdministratorUser ? true : null,
        };
    }

    /// <summary>The catalog's columns, minus HideDocumentCatalogColumns, widths from DocumentCatalogColumnsWidth.</summary>
    public async Task<List<DocumentColumn>> Columns(CancellationToken ct)
    {
        var hidden = (await settings.List("HideDocumentCatalogColumns", ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var widths = ParseWidths(await settings.Text("DocumentCatalogColumnsWidth", "", ct));
        var lastSign = await settings.Bool("EnableLastSignatureDateColumn", false, ct);
        return AllColumns
            .Where(c => !hidden.Contains(c.Key) && (c.Key != "LastSignDate" || lastSign))
            // v1 orders by the copy's creation date only (ListModel default); no other column sorts yet.
            .Select(c => new DocumentColumn { Key = c.Key, Label = c.Label, Width = widths.GetValueOrDefault(c.Key, c.Width), Sortable = c.Key == "IssueDate" ? true : null })
            .ToList();
    }

    private const string Dates = "Ημερομηνίες", Doc = "Έγγραφο", Copy = "Αντίγραφο της υπηρεσίας", People = "Πρόσωπα", Groups = "Υπηρεσίες";

    /// <summary>
    /// The criteria the list search accepts (DashboardSearchRequest → ListModel). Criteria of the Razor
    /// sidebar that it does not pass on (importance, read, priority, classification, thematic folders,
    /// movements, consultor, ΑΔΑ, …) are not offered.
    /// </summary>
    private async Task<List<FilterDef>> FilterDefs(GroupRole role, CancellationToken ct)
    {
        var statuses = await reference.Statuses(ct);
        string S(int id, string fallback) => fallback.Length > 0 ? fallback : statuses.GetValueOrDefault(id)?.Title ?? id.ToString();
        var statusOptions = new (int Id, string Label)[]
        {
            (8, "Απαντήθηκε"), (2, "Αρχειοθετήθηκε"), (1, "Δημιουργήθηκε"), (3, "Έχει αποσταλεί"), (5, "Έχει υπογραφεί"),
            (6, "Έχει λάβει τελικές υπογραφές"), (9, "Έχει υπογραφεί από τον συντάκτη"), (4, "Έχει επιστραφεί"), (7, "Χρεώθηκε"),
            (10, "Έχει κοινοποιηθεί"), (12, "Ακυρώθηκε"), (13, ""), (15, ""), (18, ""), (19, ""),
        }.Select(s => new FilterOption { Id = s.Id, Label = S(s.Id, s.Label) }).OrderBy(o => o.Label, StringComparer.CurrentCulture).ToList();

        var kinds = Trees.Flatten(Trees.Build(await reference.Kinds(ct), k => k.IdDocumentKind, k => k.ParentId, k => k.Title ?? k.Code ?? k.IdDocumentKind.ToString()));
        var categories = Trees.Flatten(Trees.Build(await reference.Categories(ct), c => c.IdCategory, c => c.IdParentCategory, c => c.Title ?? c.IdCategory.ToString()));
        var sites = (await reference.Titles("Document.PublishSite", "IdPublishSite", "[Document].[PublishSiteTitleResources]", ct, "t.IsActive = 1"))
            .Select(o => new FilterOption { Id = o.Id, Label = o.Label }).ToList();

        var list = new List<FilterDef>
        {
            new() { Key = "SendDate", Label = "Ημερομηνία Αποστολής", Group = Dates, Kind = "dates" },
            new()
            {
                Key = "ActionExpiration", Label = "Προθεσμία Ενέργειας", Group = Dates, Kind = "options", Single = true,
                Options = [new() { Id = 1, Label = "Λήγουν εντός της εβδομάδας" }, new() { Id = 2, Label = "Λήγουν εντός του μήνα" },
                    new() { Id = 3, Label = "Έχουν προθεσμία και δεν λήγουν" }, new() { Id = 4, Label = "Έχουν λήξει" }],
            },
            new() { Key = "Status", Label = "Κατάσταση", Group = Doc, Kind = "options", Options = statusOptions },
            new() { Key = "Kinds", Label = "Είδος Εγγράφου", Group = Doc, Kind = "options", Options = kinds },
            new() { Key = "Categories", Label = "Θεματικές Κατηγορίες", Group = Doc, Kind = "options", Options = categories },
            new()
            {
                Key = "Origin", Label = "Προέλευση", Group = Doc, Kind = "options",
                Options = [new() { Id = 1, Label = "Συνταχθέντα" }, new() { Id = 2, Label = "Καταχωρισθέντα" }, new() { Id = 3, Label = "Παραληφθέντα για ενέργεια" },
                    new() { Id = 4, Label = "Παραληφθέντα για κοινοποίηση" }, new() { Id = 5, Label = "Γνωστοποιήθηκαν" }, new() { Id = 6, Label = "Χρεώθηκαν" }],
            },
        };
        if (sites.Count > 0) list.Add(new() { Key = "Publications", Label = "Δημοσιεύτηκε", Group = Doc, Kind = "options", Options = sites });
        if (role.IsDirector || role.IsSecretarialSupport)
            list.Add(new() { Key = "Pending", Label = "Έγγραφα Που Εκκρεμούν", Group = Copy, Kind = "options",
                Options = [new() { Id = 1, Label = "Εκκρεμούν" }, new() { Id = 2, Label = "Χρεωμένα σε ανενεργούς χρήστες" }] });
        list.Add(new() { Key = "ExactCopies", Label = "Ακριβή Αντίγραφα", Group = Copy, Kind = "options",
            Options = [new() { Id = 1, Label = "Υπογεγραμμένα" }, new() { Id = 2, Label = "Μη Υπογεγραμμένα" }] });
        list.Add(new() { Key = "OfCopyTypes", Label = "Τύπος Εγγράφου", Group = Copy, Kind = "options",
            Options = [new() { Id = 1, Label = "Εισερχόμενο" }, new() { Id = 2, Label = "Εξερχόμενο" }, new() { Id = 3, Label = "Σχέδιο" }, new() { Id = 4, Label = "Υποστηρικτικό" }] });
        foreach (var (key, label) in new[] { ("Signator", "Υπογράφων"), ("Author", "Συντάκτης"), ("Creator", "Καταχωριστής"), ("AssignedTo", "Χρεώθηκε Στον"),
                     ("AnsweredBy", "Απαντήθηκε Από"), ("ArchivedBy", "Αρχειοθετήθηκε Από") })
            list.Add(new() { Key = key, Label = label, Group = People, Kind = "users" });
        foreach (var (key, label) in new[] { ("SentTo", "Στάλθηκε Προς"), ("SentFrom", "Στάλθηκε Από"), ("AnnouncedTo", "Κοινοποιήθηκε Προς"),
                     ("AnnouncedFrom", "Κοινοποιήθηκε Από"), ("SignatureObligationBy", "Υποχρέωση Υπογραφής"), ("CreatedForGroup", "Εκδούσα Υπηρεσιακή Μονάδα") })
            list.Add(new() { Key = key, Label = label, Group = Groups, Kind = "groups" });
        return list;
    }

    /// <summary>DocumentCatalogColumnsWidth: "Key:Width" or "Key=Width" pairs, comma separated.</summary>
    internal static Dictionary<string, int> ParseWidths(string s) =>
        s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(new[] { ':', '=' }, 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && int.TryParse(p[1].Replace("px", ""), out _))
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => int.Parse(g.First()[1].Replace("px", "")), StringComparer.OrdinalIgnoreCase);
}
