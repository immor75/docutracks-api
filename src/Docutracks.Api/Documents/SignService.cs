using System.Text.Json;
using System.Text.Json.Nodes;

namespace Docutracks.Api.Documents;

/// <summary>
/// Signing (phase 7): `GET sign-data` from SQL (the user's signature tokens), `POST sign/otp` and
/// signing through v1 `/sign/remote/document`, one request per document with the same OTP; the OTP of ΣΗΔΕ
/// through the classic HelperController.
/// </summary>
public sealed class SignService(IDb db, IReference reference, ISettings settings, V1Mobile mobile, V1Client v1, ClassicWriter writer, UserContext user)
{
    /// <summary>Common.Constants.DigitalSignatureToken.</summary>
    private const int NoDevice = 11, ManualToken = 12;

    private sealed class TokenRow
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? CodeName { get; set; }
        public bool? IsRemote { get; set; }
        public bool? SupportsMassiveSign { get; set; }
    }

    private sealed class DocTypeRow
    {
        public int IdDocument { get; set; }
        public int IdDocumentType { get; set; }
    }

    public async Task<SignForm> Form(int groupId, List<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) throw ApiException.BadRequest("Δεν έχει οριστεί έγγραφο.");
        var me = user.Required;
        foreach (var id in ids)
        {
            var r = await mobile.Get(id, groupId, ct);
            var sign = r.Actions.FirstOrDefault(a => a.Code == "UiSign");
            if (!r.CanSee || sign is not { Enabled: true })
                throw new ApiException(sign?.Reason ?? "Δεν μπορείτε να υπογράψετε το έγγραφο.");
        }
        var types = (await db.Query<DocTypeRow>("SELECT IdDocument, IdDocumentType FROM Document.Document WHERE IdDocument IN @ids", new { ids }, ct))
            .Select(t => t.IdDocumentType).Distinct().ToList();
        var allDrafts = types.All(t => t == DocumentType.Draft);

        var tokens = await db.Query<TokenRow>(
            $"""
            SELECT t.IdDigitalSignatureToken AS Id, t.CodeName, t.IsRemote, t.SupportsMassiveSign,
                   {Sql.Title("[Document].[DigitalSignatureTokenTitleResources]", "t.IdDigitalSignatureToken")} AS Title
              FROM Organization.OrgUserDigitalSignatureToken ut
             INNER JOIN Document.DigitalSignatureToken t ON t.IdDigitalSignatureToken = ut.IdDigitalSignatureToken
             WHERE ut.IdOrgUser = @userId AND t.IsActive = 1
            """, new { userId = me.Id }, ct);
        var hasNoDevice = tokens.Any(t => t.Id == NoDevice);
        var usable = tokens.Where(t => t.Id is not NoDevice and not ManualToken && (ids.Count == 1 || t.SupportsMassiveSign != false)).ToList();
        var defaultToken = await db.Scalar<int?>("SELECT DigitalSignatureTokenId FROM Organization.OrgUser WHERE IdOrgUser = @id", new { id = me.Id }, ct);
        var remote = usable.Where(t => t.IsRemote == true).ToList();
        var allowWithout = allDrafts && hasNoDevice && !await settings.Bool("IsDigitalSignMandatoryForFinalAsigneeInSxedio", false, ct);
        var useImage = await db.Scalar<bool?>("SELECT UseUserImageOnSign FROM Organization.OrgUser WHERE IdOrgUser = @id", new { id = me.Id }, ct);

        return new SignForm
        {
            Title = ids.Count == 1 ? "Υπογραφή" : $"Υπογραφή {ids.Count} εγγράφων",
            Signer = new Signer(me.FullName, await reference.GroupName(groupId, ct) ?? ""),
            Tokens = usable.Select(t => new SignToken { Id = t.Id, Title = t.Title ?? t.CodeName ?? t.Id.ToString(), IsRemote = t.IsRemote == true }).ToList(),
            DefaultTokenId = usable.Any(t => t.Id == defaultToken) ? defaultToken : remote.FirstOrDefault()?.Id,
            AllowWithoutDigital = allowWithout,
            AppendSignature = types.Any(t => t != DocumentType.Draft) && await settings.Bool("DisplayVerificationDigitalSignature", false, ct) ? new DefaultFlag(false) : null,
            SignAsFinalSigner = await settings.Bool("EapImplementationAlternativeResources", false, ct) ? new DefaultFlag(true) : null,
            UseUserImage = useImage is not null && allDrafts ? new DefaultFlag(useImage.Value) : null,
            CommentMaxLength = await settings.Int("MaxCommentLength", 1000, ct),
            Classic = remote.Count == 0 && !allowWithout
                ? "Η υπογραφή με USB token γίνεται ακόμη στο κλασικό UI."
                : null,
        };
    }

    public async Task<SignOtpPayload> Otp(SignOtpRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password)) throw new ApiException("Συμπληρώστε όνομα χρήστη και κωδικό.");
        // Only ΣΗΔΕ sends an OTP on request (HelperController.RequestOtpForSDDDSign); the others use an app or a device.
        var code = await db.Scalar<string>("SELECT CodeName FROM Document.DigitalSignatureToken WHERE IdDigitalSignatureToken = @id", new { id = req.TokenId }, ct);
        if (code is null || !code.Contains("SDDD", StringComparison.OrdinalIgnoreCase))
            return new SignOtpPayload("Ο πάροχος δεν στέλνει OTP· χρησιμοποιήστε τον κωδικό της εφαρμογής ή της συσκευής σας.");
        return new SignOtpPayload(await writer.Otp(req.GroupId, req.Username.Trim(), req.Password, ct));
    }

    public async Task<RunActionPayload> Sign(SignRequest req, CancellationToken ct)
    {
        var ids = req.DocumentIds.Distinct().ToList();
        if (ids.Count == 0) throw ApiException.BadRequest("Δεν έχει οριστεί έγγραφο.");
        if (req.Digital && (req.TokenId is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password) || string.IsNullOrWhiteSpace(req.Otp)))
            throw new ApiException("Συμπληρώστε όνομα χρήστη, κωδικό και OTP.");

        if (ids.Count > 200) throw ApiException.BadRequest("Έως 200 έγγραφα τη φορά.");

        // As the classic mass signing (SigningAppletController.SignMassiveDocuments): the same
        // credentials and OTP for each document, one request each, so a failure does not undo the others.
        var results = new List<ActionResult>();
        foreach (var id in ids)
        {
            JsonNode? res;
            try
            {
                res = await v1.Json(HttpMethod.Post, "services/sign/remote/document", new
                {
                    DocumentId = id,
                    UserId = user.Required.Id,
                    req.GroupId,
                    Comment = req.Comment ?? "",
                    IsRemote = req.Digital,
                    NonDigitalSignature = !req.Digital,
                    UserTokenId = req.TokenId ?? 0,
                    SignApiUsername = req.Username,
                    SignApiPassword = req.Password,
                    req.Otp,
                    AppendSignature = req.AppendSignature ?? false,
                    SignAsFinalSigner = req.SignAsFinalSigner ?? false,
                    UseUserImage = req.UseUserImage ?? false,
                    UseUserImageOnSign = req.UseUserImage ?? false,
                }, ct);
            }
            catch (ApiException e) when (e.Status != System.Net.HttpStatusCode.Unauthorized)
            {
                results.Add(new ActionResult { DocumentId = id, Success = false, Message = e.Message });
                continue;
            }
            var ok = res?["Success"]?.GetValue<bool>() == true;
            results.Add(new ActionResult
            {
                DocumentId = id,
                Success = ok,
                Message = ok ? null : V1Mobile.Clean(res?["ErrorInfo"]?["ErrorMessage"]?.GetValue<string>()
                                                     ?? res?["ErrorInfo"]?["ExceptionMessage"]?.GetValue<string>()) ?? "Η υπογραφή δεν ολοκληρώθηκε.",
            });
        }
        return new RunActionPayload(results);
    }
}

/// <summary>`POST flags` (read, important) and `POST export` through the classic DocumentController and ListController.</summary>
public sealed class FlagsService(V1Client v1, FiltersService filters, V1Mobile mobile, ClassicWriter writer, ClassicClient classic)
{
    public async Task<DocumentFlagsPayload> Flags(DocumentFlagsRequest req, CancellationToken ct)
    {
        if (req.DocumentIds.Count == 0) throw ApiException.BadRequest("Δεν έχει οριστεί έγγραφο.");
        if (req.IsRead is null && req.IsImportant is null) throw ApiException.BadRequest("Δεν ορίστηκε αλλαγή.");
        var ids = await Visible(req.DocumentIds, req.GroupId, ct);
        if (ids.Count == 0) throw ApiException.NotFound();
        return new DocumentFlagsPayload(await writer.Flags(req.GroupId, ids, req.IsRead, req.IsImportant, ct));
    }

    /// <summary>The documents of the list the user sees in this group (v1 formobile, a few in parallel).</summary>
    private async Task<List<int>> Visible(IEnumerable<int> documentIds, int groupId, CancellationToken ct)
    {
        var ids = documentIds.Distinct().ToList();
        if (ids.Count > 500) throw ApiException.BadRequest("Έως 500 έγγραφα τη φορά.");
        using var gate = new SemaphoreSlim(6);
        var visible = await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try { return await mobile.CanSee(id, groupId, ct) ? id : 0; }
            finally { gate.Release(); }
        }));
        return visible.Where(id => id > 0).ToList();
    }

    /// <summary>Most documents one export asks for (the list of the Razor exports what the filter finds).</summary>
    private const int MaxExport = 5000;

    public async Task<ExportDocumentsPayload> Export(ExportDocumentsRequest req, CancellationToken ct)
    {
        List<int> ids;
        if (req.DocumentIds is { Count: > 0 })
        {
            ids = await Visible(req.DocumentIds, req.GroupId, ct);
        }
        else
        {
            // The documents of the folder and filters, from the same search as the list (ListModel).
            if (req.Query is null) throw ApiException.BadRequest("Δεν ορίστηκαν έγγραφα.");
            var key = FolderKey.Parse(req.Query.FolderKey);
            var ctx = await filters.Context(req.GroupId, ct);
            if (!FolderCatalog.Keys(FolderCatalog.Build(ctx)).Contains(key.ToString())) throw ApiException.BadRequest("Ο φάκελος δεν είναι διαθέσιμος.");
            var (from, to) = SearchService.PeriodRange(req.Query.Period, DateTime.Today);
            var body = SearchService.ToV1(req.GroupId, key, req.Query.Search, from, to, req.Query.Criteria);
            body["PageNumber"] = 1;
            body["PageSize"] = MaxExport;
            body["IncludeCount"] = true;
            body["ForList"] = true;
            var res = await v1.Json(HttpMethod.Post, "services/documents/documentslistsearch/", body, ct);
            ids = (res?["Payload"]?["Data"]?.AsArray() ?? [])
                .Select(n => n?["Id"]?.GetValue<int?>() ?? n?["IdDocument"]?.GetValue<int?>() ?? 0)
                .Where(id => id > 0).Distinct().ToList();
            var total = res?["Payload"]?["Page"]?["TotalElements"]?.GetValue<int?>() ?? ids.Count;
            if (total > MaxExport) throw new ApiException($"Η εξαγωγή γίνεται έως {MaxExport} έγγραφα· περιορίστε τα φίλτρα ({total} έγγραφα).");
        }
        if (ids.Count == 0) throw new ApiException("Δεν βρέθηκαν έγγραφα για εξαγωγή.");
        // The anti-forgery token: from the form of «Νέο έγγραφο», or the view of the first document.
        await writer.Export(req.GroupId, ids, req.WithFiles == true,
            classic.Url("Document", req.GroupId, "UiNew/UiNew"), ct, classic.Url("Document", req.GroupId, $"UiViewDoc/{ids[0]}"));
        return new ExportDocumentsPayload(ids.Count);
    }
}
