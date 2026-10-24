namespace Docutracks.Api.Documents;

/// <summary>Common.Constants.GroupVisibilityEnum (the order is the int value).</summary>
public enum GroupVisibility
{
    Global, All, Group, GroupOwned, SubGroups, AssignedToUser, OwnedByDirectorate, ExactCopiesVerified, ExactCopiesNotVerified,
    NotifiedCopiesVerified, NotifiedCopiesNotVerified, OpenDocuments, ConsultedByMe,
}

/// <summary>What a folder key stands for: the query string of the Razor sidebar links.</summary>
public sealed record FolderKey(int DocType, int FolderType, GroupVisibility Visibility)
{
    /// <summary>"{DocType}.{FolderType}.{Visibility}", e.g. "1.11.GroupOwned" (Εισερχόμενα › Εκκρεμή). DocType 0: none.</summary>
    public override string ToString() => $"{DocType}.{FolderType}.{Visibility}";

    public static FolderKey Parse(string? key)
    {
        var parts = (key ?? "").Split('.');
        if (parts.Length == 3
            && int.TryParse(parts[0], out var docType)
            && int.TryParse(parts[1], out var folderType)
            && Enum.TryParse<GroupVisibility>(parts[2], ignoreCase: false, out var vis)
            && Enum.IsDefined(vis))
            return new FolderKey(docType, folderType, vis);
        throw ApiException.BadRequest("Άγνωστος φάκελος.");
    }
}

/// <summary>
/// The sidebar of the Razor list (ListModel.FolderTypes, DocumentFilters.cshtml) for a user in a
/// group: only the folders with Display = true.
/// </summary>
public static class FolderCatalog
{
    // SearchConstants.T_*
    private const int InOutstanding = 11, InCharged = 9, InChargedForAnswer = -9, InChargedToMe = 30, InChargedToMeForAnswer = -30,
        InInformed = 31, InReplied = 12, InArchived = 8, InAnnounced = 10, InSent = 14, InReturned = 13, InReturnedByGroups = 37, InReturnedToMe = 33;
    private const int OutSent = 17, OutReplied = 23, OutNotSent = 16, OutArchived = 15, OutReturned = 24, OutReturnedByGroups = 38, OutSigned = 18;
    private const int SxWithSignatureStatus = 40, SxToConsult = -412, SxConsultApproved = -411, SxConsultApprovedWithRemarks = -410, SxConsultReturned = -409,
        SxToSign = 1, SxToSignByMe = 23, SxFinalSigned = 5, SxToSignByManager = 41, SxToSignBySubordinates = 2, SxArchived = 4, SxSigned = 3,
        SxSignedByMe = 22, SxReturnedByMe = 7, SxReturned = 6;
    private const int SignedAll = 34, SignedOutgoing = 35, SignedDrafts = 36, ConsultedByMe = -408, CoAuthorInOut = 20, CoAuthorDrafts = 21,
        NotifiedAll = 25, NotifiedRead = 26, NotifiedUnread = 27, NotifiedByMe = 42, ExactSigned = 28, ExactUnsigned = 29, Published = 32,
        RequestsOpen = 37, RequestsApproved = 38, RequestsNotApproved = 39;

    public sealed record Context(GroupRole Role, bool IsForAnswerEnabled, bool ShowSignaturesStatus, bool DisplayOutgoingReturned,
        bool ShowOpenDocuments, bool ShowRequestsFilter, bool HasSubGroups, bool GroupHasActiveConsultant);

    public static string DefaultKey(Context c) =>
        new FolderKey(0, 0, c.ShowOpenDocuments ? GroupVisibility.OpenDocuments : GroupVisibility.GroupOwned).ToString();

    public static List<DocumentFolder> Build(Context c)
    {
        var r = c.Role;
        bool director = r.IsDirector, secretary = r.IsSecretarialSupport, protocol = r.IsProtocolUser;
        var list = new List<DocumentFolder>
        {
            new() { Key = DefaultKey(c), Label = c.ShowOpenDocuments ? "Εκκρεμή" : "Κύρια" },
        };

        DocumentFolder Leaf(int docType, int folderType, string label, GroupVisibility vis) =>
            new() { Key = new FolderKey(docType, folderType, vis).ToString(), Label = label };

        DocumentFolder Section(string label, int docType, bool displayAll, IEnumerable<(bool Show, int Folder, string Label, GroupVisibility Vis)> items)
        {
            var children = items.Where(i => i.Show).Select(i => Leaf(docType, i.Folder, i.Label, i.Vis)).ToList();
            return new DocumentFolder
            {
                Key = displayAll ? new FolderKey(docType, 0, GroupVisibility.GroupOwned).ToString() : null,
                Label = label,
                Children = children,
            };
        }

        const GroupVisibility Own = GroupVisibility.GroupOwned, Global = GroupVisibility.Global;
        list.Add(Section("Εισερχόμενα", DocumentType.Incoming, true,
        [
            (true, InOutstanding, "Εκκρεμή", Own),
            (director || secretary, InCharged, "Χρεωμένα", Own),
            ((director || secretary) && c.IsForAnswerEnabled, InChargedForAnswer, "Χρεωμένα προς Απάντηση", Own),
            (true, InChargedToMe, "Χρεωμένα σε εμένα", Own),
            (c.IsForAnswerEnabled, InChargedToMeForAnswer, "Χρεωμένα σε εμένα προς Απάντηση", Own),
            (true, InInformed, "Από Ενημέρωση", Own),
            (true, InReplied, "Απαντημένα", Own),
            (true, InArchived, "Αρχειοθετημένα", Own),
            (director || secretary || protocol, InAnnounced, "Κοινοποιηθέντα", Own),
            (true, InSent, "Απεσταλμένα", Own),
            (director || secretary || protocol, InReturned, "Επιστραφέντα", Own),
            (true, InReturnedByGroups, "Επιστραφέντα από υπηρεσίες", Own),
            (true, InReturnedToMe, "Επιστραφέντα σε εμένα", Own),
        ]));
        list.Add(Section("Εξερχόμενα", DocumentType.Outgoing, true,
        [
            (true, OutSent, "Απεσταλμένα", Own),
            (true, OutReplied, "Απαντημένα", Own),
            (true, OutNotSent, "Μη απεσταλμένα", Own),
            (true, OutArchived, "Αρχειοθετημένα", Own),
            (c.DisplayOutgoingReturned, OutReturned, "Επιστραφέντα", Own),
            (true, OutReturnedByGroups, "Επιστραφέντα από υπηρεσίες", Own),
            (true, OutSigned, "Υπογεγραμμένα", Own),
        ]));
        list.Add(Section("Σχέδια", DocumentType.Draft, false,
        [
            (true, 0, "Όλα", Global),
            (c.ShowSignaturesStatus, SxWithSignatureStatus, "Στάδιο Υπογραφών", Global),
            ((director || secretary || r.IsConsultor) && c.GroupHasActiveConsultant, SxToConsult, "Προς Γνώμη", Global),
            (director || secretary || r.IsConsultor, SxConsultApproved, "Εγκεκριμένα", Global),
            (director || secretary || r.IsConsultor, SxConsultApprovedWithRemarks, "Εγκεκριμένα με επισημάνσεις", Global),
            (director || secretary || r.IsConsultor, SxConsultReturned, "Προς Επιστροφή", Global),
            (director || secretary || protocol, SxToSign, "Προς υπογραφή", Global),
            (true, SxToSignByMe, "Προς υπογραφή από μένα", Global),
            (true, SxFinalSigned, "Με τελική υπογραφή", Global),
            (secretary, SxToSignByManager, "Προς υπογραφή από προϊστάμενο", Global),
            (director || secretary || protocol, SxToSignBySubordinates, "Υφισταμένων Υπηρεσιών", Global),
            (true, SxArchived, "Αρχειοθετημένα", GroupVisibility.Group),
            (director || secretary || protocol, SxSigned, "Υπογεγραμμένα", Global),
            (true, SxSignedByMe, "Υπογεγραμμένα από μένα", Global),
            (true, SxReturnedByMe, "Επιστραφέντα από μένα", Global),
            (director || secretary || protocol, SxReturned, "Επιστραφέντα από ανώτερους", Global),
        ]));
        list.Add(Section("Υπογεγραμμένα", -9994, false,
        [
            (true, SignedAll, "Όλα", Global),
            (true, SignedOutgoing, "Εξερχόμενα", Global),
            (true, SignedDrafts, "Σχέδια", Global),
        ]));
        if (r.IsConsultor)
            list.Add(new DocumentFolder { Key = new FolderKey(0, ConsultedByMe, GroupVisibility.ConsultedByMe).ToString(), Label = "Έγγραφα Συμβούλου" });
        list.Add(Section("Συνεισηγήσεις", -9999, false,
        [
            (true, CoAuthorInOut, "Εισερχόμενα/Εξερχόμενα", Global),
            (true, CoAuthorDrafts, "Σχέδια", Global),
        ]));
        list.Add(Section("Γνωστοποιηθέντα", -9998, false,
        [
            (true, NotifiedAll, "Όλα", Global),
            (true, NotifiedRead, "Αναγνωσμένα", Global),
            (true, NotifiedUnread, "Μη Αναγνωσμένα", Global),
            (true, NotifiedByMe, "Από εμένα", Global),
        ]));
        if (r.Has(Roles.CopyVerifier))
            list.Add(Section("Ακριβή Αντίγραφα", -9997, false,
            [
                (true, ExactSigned, "Υπογεγραμμένα", GroupVisibility.NotifiedCopiesVerified),
                (true, ExactUnsigned, "Μη Υπογεγραμμένα", GroupVisibility.NotifiedCopiesNotVerified),
            ]));
        list.Add(Leaf(-9996, Published, "Δημοσιευμένα", Global));
        if (c.ShowRequestsFilter && r.IsProtocolUser)
            list.Add(Section("Αιτήματα-Άδειες", -9991, true,
            [
                (true, RequestsOpen, "Εκκρεμή", Global),
                (true, RequestsApproved, "Εγκεκριμένα", Global),
                (true, RequestsNotApproved, "Μη Εγκεκριμένα", Global),
            ]));
        if (c.HasSubGroups)
            list.Add(new DocumentFolder
            {
                Key = new FolderKey(0, 0, GroupVisibility.SubGroups).ToString(),
                Label = "Υφισταμένων υπηρεσιών",
                Children =
                [
                    Leaf(DocumentType.Incoming, 0, "Εισερχόμενα", GroupVisibility.SubGroups),
                    Leaf(DocumentType.Outgoing, 0, "Εξερχόμενα", GroupVisibility.SubGroups),
                    Leaf(DocumentType.Draft, 0, "Σχέδια", GroupVisibility.SubGroups),
                ],
            });
        return list;
    }

    /// <summary>Every key of the tree, to refuse keys that this user may not open.</summary>
    public static IEnumerable<string> Keys(IEnumerable<DocumentFolder> folders) =>
        folders.SelectMany(f => (f.Key is null ? [] : new[] { f.Key }).Concat(Keys(f.Children ?? [])));
}
