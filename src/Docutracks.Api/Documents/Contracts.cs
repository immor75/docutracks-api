using System.Text.Json;
using System.Text.Json.Serialization;

namespace Docutracks.Api.Documents;

// The contracts of docs/documents-api.md, one to one with src/app/core/documents-models.ts of the
// Angular app. Names are PascalCase as in the TypeScript; null fields are not written.

public static class DocumentType
{
    public const int Incoming = 1, Outgoing = 2, Draft = 3, Supporting = 4;
}

public sealed record Option(int Id, string Label);

public sealed class FilterOption
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
    public int? Depth { get; init; }
}

/* ---------- GET filters ---------- */

public sealed class DocumentFolder
{
    public string? Key { get; init; }
    public string Label { get; init; } = "";
    public List<DocumentFolder>? Children { get; init; }
}

public sealed class DocumentColumn
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public int? Width { get; init; }
    public bool? Sortable { get; init; }
}

public sealed class FilterDef
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Group { get; init; } = "";
    /// <summary>options | users | groups | dates | text</summary>
    public string Kind { get; init; } = "options";
    public List<FilterOption>? Options { get; init; }
    public bool? Single { get; init; }
}

public sealed record DocumentSort(string Column, string Direction);

public sealed class DocumentFiltersPayload
{
    public List<DocumentFolder> Folders { get; init; } = [];
    public string DefaultFolderKey { get; init; } = "";
    public List<Option> SearchTypes { get; init; } = [];
    public int DefaultSearchType { get; init; }
    public List<DocumentColumn> Columns { get; init; } = [];
    public List<Option> Periods { get; init; } = [];
    public int DefaultPeriod { get; init; }
    public DocumentSort DefaultSort { get; init; } = new("IssueDate", "desc");
    public List<FilterDef> Filters { get; init; } = [];
    public bool? CanExportWithFiles { get; init; }
}

/* ---------- POST search ---------- */

public sealed class SearchText
{
    public string Text { get; init; } = "";
    public int Type { get; init; }
}

public sealed class Period
{
    public int Range { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
}

public sealed class SearchDocumentsRequest
{
    public int GroupId { get; init; }
    public string FolderKey { get; init; } = "";
    public SearchText? Search { get; init; }
    public Period Period { get; init; } = new();
    public DocumentSort? Sort { get; init; }
    /// <summary>Values: number[], string, or { From, To }.</summary>
    public Dictionary<string, JsonElement>? Criteria { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public sealed class DocumentRow
{
    public int Id { get; init; }
    public int IdDocumentCopy { get; init; }
    public int Type { get; init; }
    public string Title { get; init; } = "";
    public string IssueDate { get; init; } = "";
    public string? LastSignatureDate { get; init; }
    public string? ProtocolText { get; init; }
    public string? ProtocolDate { get; init; }
    public string? SenderProtocol { get; init; }
    public string? Sender { get; init; }
    public string? Recipients { get; init; }
    public string? SubKind { get; init; }
    public string Status { get; init; } = "";
    public bool IsFinal { get; init; }
    public string? SignaturesStatus { get; init; }
    public string? NextToSign { get; set; }
    public string? Kind { get; init; }
    public string? Assignees { get; init; }
    public string? Folders { get; init; }
    public string? Endiaferomenos { get; init; }
    public string? CreatedBy { get; init; }
    public bool IsUnread { get; init; }
    public bool IsReadByUser { get; init; }
    public bool IsImportant { get; init; }
    public bool IsUrgent { get; init; }
    public bool HasComments { get; init; }
    public string? KeepUntil { get; init; }
}

public sealed record PageInfo(int Size, int PageNumber, int TotalElements);

public sealed record AppliedPeriod(string? From, string To);

public sealed class SearchDocumentsPayload
{
    public List<DocumentRow> Data { get; init; } = [];
    public PageInfo Page { get; init; } = new(20, 1, 0);
    public List<string> Columns { get; init; } = [];
    public AppliedPeriod Period { get; init; } = new(null, "");
}

/* ---------- GET {id} ---------- */

public sealed class DocumentFile
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public long? Size { get; init; }
    /// <summary>main | attachment | internal | signer</summary>
    public string Role { get; init; } = "main";
    public bool IsPdf { get; init; }
    public bool? IsSigned { get; init; }
}

public sealed class DocumentRecipient
{
    public string Name { get; init; } = "";
    public bool Internal { get; init; }
    public bool? Cc { get; init; }
}

public sealed class DocumentSignature
{
    public string Name { get; init; } = "";
    public string? Group { get; init; }
    public string? SignedAt { get; init; }
    public string? Role { get; init; }
}

public sealed class DocumentCopyRow
{
    public int Id { get; init; }
    public string CreatedAt { get; init; } = "";
    public string CreatedByGroup { get; init; } = "";
    public string OwnerGroup { get; init; } = "";
    public string? AssignedTo { get; init; }
    public string Status { get; init; } = "";
    public string? ProtocolText { get; init; }
}

public sealed class RelatedDocument
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public int Type { get; init; }
    public string? ProtocolText { get; init; }
    public string? Date { get; init; }
    public string Relation { get; init; } = "";
    public bool CanOpen { get; init; }
}

public sealed class DocumentInFolder
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? CaseNumber { get; init; }
}

public sealed class DocumentView
{
    public int Id { get; init; }
    public int IdDocumentCopy { get; init; }
    public int Type { get; init; }
    public string Title { get; init; } = "";
    public string? Kind { get; init; }
    public string? SubKind { get; init; }
    public string Status { get; init; } = "";
    public bool IsFinal { get; init; }
    public string IssueDate { get; init; } = "";
    public string? ProtocolText { get; init; }
    public string? ProtocolDate { get; init; }
    public string? ProtocolBook { get; init; }
    public string? SenderProtocol { get; init; }
    public string? Sender { get; init; }
    public string? SendDate { get; init; }
    public List<DocumentRecipient> Recipients { get; init; } = [];
    public string? Priority { get; init; }
    public string? Classification { get; init; }
    public bool? ContainsPersonalData { get; init; }
    public string? DecisionNumber { get; init; }
    public string? Ada { get; init; }
    public string? Comments { get; init; }
    public string? CreatedBy { get; init; }
    public string? CreatedForGroup { get; init; }
    public string? Author { get; init; }
    public string? Endiaferomenos { get; init; }
    public string? KeepUntil { get; init; }
    public List<string> Categories { get; init; } = [];
    public List<string> ThematicFolders { get; init; } = [];
    public List<DocumentFile> Files { get; init; } = [];
    public List<DocumentSignature> Signatures { get; init; } = [];
    public List<DocumentCopyRow> Copies { get; init; } = [];
    public List<RelatedDocument> Related { get; init; } = [];
    public List<DocumentInFolder> Folders { get; init; } = [];
    public bool IsImportant { get; init; }
    public bool IsUrgent { get; init; }
}

public sealed class DocumentHistoryEntry
{
    public string At { get; init; } = "";
    public string Action { get; init; } = "";
    public string? User { get; init; }
    public string? Group { get; init; }
    public string? Target { get; init; }
    public string? Comment { get; init; }
}

/* ---------- flags, export ---------- */

public sealed class DocumentFlagsRequest
{
    public int GroupId { get; init; }
    public List<int> DocumentIds { get; init; } = [];
    public bool? IsRead { get; init; }
    public bool? IsImportant { get; init; }
}

public sealed record DocumentFlagsPayload(List<int> Updated);

public sealed class ExportQuery
{
    public string FolderKey { get; init; } = "";
    public SearchText? Search { get; init; }
    public Period Period { get; init; } = new();
    public DocumentSort? Sort { get; init; }
    public Dictionary<string, JsonElement>? Criteria { get; init; }
}

public sealed class ExportDocumentsRequest
{
    public int GroupId { get; init; }
    public List<int>? DocumentIds { get; init; }
    public ExportQuery? Query { get; init; }
    public bool? WithFiles { get; init; }
}

public sealed record ExportDocumentsPayload(int Count);

/* ---------- actions ---------- */

public sealed class DocumentAction
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }
    public string? Reason { get; init; }
    public bool? Mass { get; init; }
}

public sealed class DocumentActionsRequest
{
    public int GroupId { get; init; }
    public List<int> DocumentIds { get; init; } = [];
}

public sealed record DocumentActions(int DocumentId, List<DocumentAction> Actions);

public sealed record DocumentActionsPayload(List<DocumentActions> Documents);

/// <summary>A field of an action form; Kind decides which properties apply (comment, select, checkbox, people, thematicFolders).</summary>
public sealed class ActionField
{
    public string Key { get; init; } = "";
    public string Kind { get; init; } = "comment";
    public string Label { get; init; } = "";
    public bool? Required { get; init; }
    public string? RequiredMessage { get; init; }
    public string? Hint { get; init; }
    public int? MaxLength { get; init; }
    public List<SelectOption>? Options { get; init; }
    public string? Pick { get; init; }
    public bool? Multi { get; init; }
    public JsonNodeValue? Tree { get; init; }
    public PerItem? PerItem { get; init; }
    public bool? AllowNew { get; init; }
    public object? Value { get; init; }
}

public sealed class SelectOption
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
    public bool? Disabled { get; init; }
    public string? Reason { get; init; }
}

public sealed class PerItem
{
    public List<Option>? EnergeiaTypes { get; init; }
    public int? DefaultEnergeiaType { get; init; }
    public bool? Urgent { get; init; }
}

public sealed class ActionForm
{
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Message { get; init; }
    public List<string>? Warnings { get; init; }
    public string Submit { get; init; } = "";
    public bool? Danger { get; init; }
    public List<ActionField> Fields { get; init; } = [];
    public string? Classic { get; init; }
}

public sealed class PersonValue
{
    public int? UserId { get; init; }
    public int GroupId { get; init; }
    public int? EnergeiaTypeId { get; init; }
    public bool? Urgent { get; init; }
}

public sealed class ThematicFolder
{
    public int? Id { get; init; }
    public string Name { get; init; } = "";
    public string? Path { get; init; }
}

public sealed class RunActionRequest
{
    public int GroupId { get; init; }
    public string Code { get; init; } = "";
    public List<int> DocumentIds { get; init; } = [];
    public Dictionary<string, JsonElement> Values { get; init; } = [];
}

public sealed class ActionResult
{
    public int DocumentId { get; init; }
    public bool Success { get; init; }
    public string? Message { get; init; }
    public int? NewDocumentId { get; init; }
}

public sealed record RunActionPayload(List<ActionResult> Results);

/* ---------- signing ---------- */

public sealed class SignToken
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public bool IsRemote { get; init; }
    public int? OtpLength { get; init; }
}

public sealed record Signer(string Name, string Group);

public sealed record DefaultFlag(bool Default);

public sealed class SignForm
{
    public string Title { get; init; } = "";
    public Signer Signer { get; init; } = new("", "");
    public List<SignToken> Tokens { get; init; } = [];
    public int? DefaultTokenId { get; init; }
    public bool AllowWithoutDigital { get; init; }
    public DefaultFlag? AppendSignature { get; init; }
    public DefaultFlag? SignAsFinalSigner { get; init; }
    public DefaultFlag? UseUserImage { get; init; }
    public int CommentMaxLength { get; init; }
    public string? Classic { get; init; }
}

public sealed class SignOtpRequest
{
    public int GroupId { get; init; }
    public int TokenId { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
}

public sealed record SignOtpPayload(string Message);

public sealed class SignRequest
{
    public int GroupId { get; init; }
    public List<int> DocumentIds { get; init; } = [];
    public bool Digital { get; init; }
    public int? TokenId { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Otp { get; init; }
    public string? Comment { get; init; }
    public bool? AppendSignature { get; init; }
    public bool? SignAsFinalSigner { get; init; }
    public bool? UseUserImage { get; init; }
}

/* ---------- new document (phase 8) ---------- */

public sealed class NewDocType
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public int DocumentType { get; init; }
}

public sealed class SourceDocument
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string? ProtocolText { get; init; }
}

public sealed class NewTypesPayload
{
    public List<NewDocType> Types { get; init; } = [];
    public bool AllowOld { get; init; }
    public SourceDocument? Source { get; init; }
}

public sealed record Tab(string Key, string Label);

public sealed class RegisterForm
{
    public int NewType { get; init; }
    public int DocumentType { get; init; }
    public string Title { get; set; } = "";
    public List<Tab> Tabs { get; init; } = [];
    public bool AllowBrief { get; set; }
    public bool BriefDefault { get; set; }
    public List<RegisterField> Fields { get; set; } = [];
    public int? DocumentId { get; set; }
    public SourceDocument? Source { get; set; }
    public string? Mode { get; set; }
    public string? Notice { get; set; }
}

/// <summary>
/// A field of the registration form. One class for all kinds (text, date, number, select, checkbox,
/// tree, people, contacts, documents, files, thematicFolders, kindForm, metadata, signatureChain, display);
/// the properties of other kinds stay null and are not written.
/// </summary>
public sealed class RegisterField
{
    public string Key { get; init; } = "";
    public string Kind { get; init; } = "text";
    public string Label { get; init; } = "";
    public string Tab { get; init; } = "basic";
    public bool? Brief { get; set; }
    public bool? Required { get; set; }
    public string? RequiredMessage { get; set; }
    public string? Hint { get; set; }
    public string? Section { get; init; }
    public HiddenIf? HiddenIf { get; set; }
    public bool? ReadOnly { get; set; }

    // text
    public int? MaxLength { get; init; }
    public bool? Multiline { get; init; }
    public List<string>? Suggestions { get; init; }
    // number
    public int? Min { get; init; }
    // select, tree
    public bool? Multi { get; init; }
    public List<Option>? Options { get; init; }
    public string? Source { get; init; }
    /// <summary>tree: List&lt;TreeOption&gt; (Source "options"); people: the organisation tree (JsonNodeValue).</summary>
    public object? Tree { get; init; }
    // people
    public string? Pick { get; init; }
    public List<int>? AllowedGroups { get; init; }
    // contacts, thematic folders
    public bool? AllowNew { get; init; }
    // files
    public string? Role { get; init; }
    public string? Accept { get; init; }
    public int? MaxSizeMb { get; init; }
    // kindForm
    public string? KindField { get; init; }
    // metadata
    public List<MetadataClass>? Classes { get; init; }
    // signatureChain
    public ChainFrom? From { get; init; }

    /// <summary>Current or default value (any kind).</summary>
    public object? Value { get; set; }
}

public sealed record HiddenIf(string Key, object? Value);

public sealed record MetadataClass(string Code, string Title);

public sealed record ChainFrom(string Finals, string? Extra, string? CoAuthors, string? Ratifiers);

public sealed class TreeOption
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public bool? Selectable { get; init; }
    public bool? HasForm { get; init; }
    public List<TreeOption>? Children { get; init; }
}

/// <summary>A related document in the forms (`documents` fields).</summary>
public sealed class RelatedValue
{
    public int Id { get; init; }
    public string? Title { get; init; }
    public int? RelationId { get; init; }
}

public sealed class ContactValue
{
    public int? Id { get; init; }
    public string Name { get; init; } = "";
    public string? Email { get; init; }
}

public sealed class UploadedFile
{
    public string? TempId { get; init; }
    public int? FileId { get; init; }
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public bool IsPdf { get; init; }
}

public sealed class RegisterRequest
{
    public int GroupId { get; init; }
    public int NewType { get; init; }
    public bool? Old { get; init; }
    public bool Brief { get; init; }
    public Dictionary<string, JsonElement> Values { get; init; } = [];
    public int? From { get; init; }
    public string? Mode { get; init; }
}

public sealed class EditRequest
{
    public int GroupId { get; init; }
    public Dictionary<string, JsonElement> Values { get; init; } = [];
}

public sealed record FieldError(string Key, string Message);

public sealed class RegisterPayload
{
    public int? DocumentId { get; init; }
    public string? ProtocolText { get; init; }
    public List<FieldError>? Errors { get; init; }
}

public sealed class SignatureChainRequest
{
    public int GroupId { get; init; }
    public List<PersonValue> Finals { get; init; } = [];
    public List<PersonValue>? Extra { get; init; }
    public List<PersonValue>? CoAuthors { get; init; }
    public List<PersonValue>? Ratifiers { get; init; }
}

public sealed class SignatureStep
{
    public int Order { get; init; }
    public string Kind { get; init; } = "";
    public string Group { get; init; } = "";
    public string? User { get; init; }
    public bool Auto { get; init; }
}

public sealed class SignatureChainPayload
{
    public List<SignatureStep> Steps { get; init; } = [];
    public List<string>? Warnings { get; init; }
}

/// <summary>A JSON value passed through as is (the organisation tree of v1).</summary>
[JsonConverter(typeof(JsonNodeValueConverter))]
public sealed record JsonNodeValue(System.Text.Json.Nodes.JsonNode? Node);

public sealed class JsonNodeValueConverter : JsonConverter<JsonNodeValue>
{
    public override JsonNodeValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(System.Text.Json.Nodes.JsonNode.Parse(ref reader));

    public override void Write(Utf8JsonWriter writer, JsonNodeValue value, JsonSerializerOptions options)
    {
        if (value.Node is null) writer.WriteNullValue();
        else value.Node.WriteTo(writer, options);
    }
}
