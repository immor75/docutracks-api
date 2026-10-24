namespace Docutracks.Api.Documents;

/// <summary>
/// Routes of `/v2/documents/*` (behind the IIS application `/api`: `/api/v2/documents/*`), one to
/// one with docs/documents-api.md. Every call names its GroupId and needs a role in it.
/// </summary>
public static class DocumentsEndpoints
{
    public static IServiceCollection AddDocuments(this IServiceCollection s) => s
        .AddSingleton<TempFiles>()
        .AddScoped<ClassicWriter>()
        .AddScoped<V1Mobile>()
        .AddScoped<FiltersService>()
        .AddScoped<SearchService>()
        .AddScoped<ViewService>()
        .AddScoped<ActionsService>()
        .AddScoped<FlagsService>()
        .AddScoped<SignService>()
        .AddScoped<RegisterForms>()
        .AddScoped<DocumentValues>()
        .AddScoped<RegisterService>();

    private static List<int> Ids(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var i) ? i : throw ApiException.BadRequest("Λανθασμένα έγγραφα (DocumentIds)."))
            .Distinct().ToList();

    public static void MapDocuments(this RouteGroupBuilder v2)
    {
        var g = v2.MapGroup("/documents");

        /* ---------- list (phases 2, 3) ---------- */

        g.MapGet("/filters", async (int groupId, IGroupAccess access, FiltersService svc, CancellationToken ct) =>
        {
            await access.Ensure(groupId, ct);
            return Envelope.Ok(await svc.Get(groupId, ct));
        });

        g.MapPost("/search", async (SearchDocumentsRequest req, IGroupAccess access, SearchService svc, CancellationToken ct) =>
        {
            await access.Ensure(req.GroupId, ct);
            return Envelope.Ok(await svc.Search(req, ct));
        });

        /* ---------- view (phase 4) ---------- */

        g.MapGet("/{id:int}", async (int id, int groupId, IGroupAccess access, ViewService svc, CancellationToken ct) =>
        {
            await access.Ensure(groupId, ct);
            return Envelope.Ok(await svc.View(id, groupId, ct));
        });

        g.MapGet("/{id:int}/history", async (int id, int groupId, IGroupAccess access, ViewService svc, CancellationToken ct) =>
        {
            await access.Ensure(groupId, ct);
            return Envelope.Ok(await svc.History(id, groupId, ct));
        });

        g.MapGet("/{id:int}/files", async (int id, int groupId, IGroupAccess access, ViewService svc, CancellationToken ct) =>
        {
            await access.Ensure(groupId, ct);
            return await svc.Zip(id, groupId, ct);
        });

        g.MapPost("/actions", async (DocumentActionsRequest req, IGroupAccess access, ActionsService svc, CancellationToken ct) =>
        {
            await access.Ensure(req.GroupId, ct);
            return Envelope.Ok(await svc.Actions(req, ct));
        });

        /* ---------- simple actions (phase 5) ---------- */

        g.MapPost("/flags", async (DocumentFlagsRequest req, IGroupAccess access, FlagsService svc, CancellationToken ct) =>
        {
            await access.Ensure(req.GroupId, ct);
            return Envelope.Ok(await svc.Flags(req, ct));
        });

        g.MapPost("/export", async (ExportDocumentsRequest req, IGroupAccess access, FlagsService svc, CancellationToken ct) =>
        {
            await access.Ensure(req.GroupId, ct);
            return Envelope.Ok(await svc.Export(req, ct));
        });

        /* ---------- workflow actions (phase 6) ---------- */

        g.MapGet("/action-data/{code}", async (string code, int groupId, string? documentIds, IGroupAccess access, ActionsService svc, CancellationToken ct) =>
        {
            await access.Ensure(groupId, ct);
            return Envelope.Ok(await svc.Form(code, groupId, Ids(documentIds), ct));
        });

        g.MapPost("/action", async (RunActionRequest req, IGroupAccess access, ActionsService svc, CancellationToken ct) =>
        {
            await access.Ensure(req.GroupId, ct);
            return Envelope.Ok(await svc.Run(req, ct));
        });

        g.MapGet("/thematic-folders", async (int groupId, string? search, IGroupAccess access, ActionsService svc, CancellationToken ct) =>
        {
            await access.Ensure(groupId, ct);
            return Envelope.Ok(await svc.ThematicFolders(groupId, search, ct));
        });

        /* ---------- signing (phase 7) ---------- */

        g.MapGet("/sign-data", async (int groupId, string? documentIds, IGroupAccess access, SignService svc, CancellationToken ct) =>
        {
            await access.Ensure(groupId, ct);
            return Envelope.Ok(await svc.Form(groupId, Ids(documentIds), ct));
        });

        g.MapPost("/sign/otp", async (SignOtpRequest req, IGroupAccess access, SignService svc, CancellationToken ct) =>
        {
            await access.Ensure(req.GroupId, ct);
            return Envelope.Ok(await svc.Otp(req, ct));
        });

        g.MapPost("/sign", async (SignRequest req, IGroupAccess access, SignService svc, CancellationToken ct) =>
        {
            await access.Ensure(req.GroupId, ct);
            return Envelope.Ok(await svc.Sign(req, ct));
        });

        /* ---------- new document, edit (phase 8) ---------- */

        g.MapGet("/new-types", async (int groupId, int? from, string? mode, IGroupAccess access, RegisterService svc, CancellationToken ct) =>
        {
            await access.Ensure(groupId, ct);
            return Envelope.Ok(await svc.NewTypes(groupId, from, mode, ct));
        });

        g.MapGet("/register-form", async (int groupId, int newType, bool? old, int? from, string? mode, IGroupAccess access, RegisterService svc, CancellationToken ct) =>
        {
            await access.Ensure(groupId, ct);
            return Envelope.Ok(await svc.Form(groupId, newType, old == true, from, mode, ct));
        });

        g.MapGet("/kinds", async (int groupId, int type, IGroupAccess access, RegisterService svc, CancellationToken ct) =>
        {
            await access.Ensure(groupId, ct);
            return Envelope.Ok(await svc.Kinds(type, ct));
        });

        g.MapGet("/kinds/{id:int}/form", async (int id, RegisterService svc, CancellationToken ct) =>
            Envelope.Ok(await svc.KindForm(id, ct)));

        g.MapGet("/metadata-classes/{code}/form", (string code) =>
            Results.Json(Envelope.Fail("Οι κλάσεις μεταδεδομένων γίνονται ακόμη στο κλασικό UI."), Json.Options));

        g.MapPost("/files", async (HttpRequest http, IGroupAccess access, RegisterService svc, CancellationToken ct) =>
        {
            if (!http.HasFormContentType) throw ApiException.BadRequest("Αναμενόταν αρχείο (multipart/form-data).");
            var form = await http.ReadFormAsync(ct);
            if (!int.TryParse(form["GroupId"], out var groupId)) throw ApiException.BadRequest("Λείπει η υπηρεσία (GroupId).");
            await access.Ensure(groupId, ct);
            var file = form.Files.GetFile("File") ?? form.Files.FirstOrDefault() ?? throw new ApiException("Δεν στάλθηκε αρχείο.");
            return Envelope.Ok(await svc.Upload(file, ct));
        }).DisableAntiforgery();

        g.MapPost("/signature-chain", async (SignatureChainRequest req, IGroupAccess access, RegisterService svc, CancellationToken ct) =>
        {
            await access.Ensure(req.GroupId, ct);
            return Envelope.Ok(await svc.SignatureChain(req, ct));
        });

        g.MapPost("/register", async (RegisterRequest req, IGroupAccess access, RegisterService svc, CancellationToken ct) =>
        {
            await access.Ensure(req.GroupId, ct);
            return Envelope.Ok(await svc.Register(req, ct));
        });

        g.MapGet("/{id:int}/edit-form", async (int id, int groupId, IGroupAccess access, RegisterService svc, CancellationToken ct) =>
        {
            await access.Ensure(groupId, ct);
            return Envelope.Ok(await svc.EditForm(id, groupId, ct));
        });

        g.MapPost("/{id:int}/edit", async (int id, EditRequest req, IGroupAccess access, RegisterService svc, CancellationToken ct) =>
        {
            await access.Ensure(req.GroupId, ct);
            return Envelope.Ok(await svc.Edit(id, req, ct));
        });
    }
}
