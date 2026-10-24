using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Docutracks.Api.Documents;

/// <summary>
/// v1 `POST /services/document/get/formobile` (DocumentGetForMobileService): whether the user may
/// see a document (View().CanExecute) and which actions of UIViewDoc can run (IUiAction.CanExecute,
/// as UIActionsHelper). `UserId` always comes from the identity, never from the client.
/// </summary>
public sealed class V1Mobile(V1Client v1, UserContext user)
{
    public sealed record Result(bool CanSee, string? Error, List<DocumentAction> Actions);

    /// <summary>Actions offered for several documents from the list (UIActionsHelper._availableMassiveUiActions).</summary>
    public static readonly HashSet<string> Mass =
    [
        "UiSign", "UiArchive", "UiSend", "UiSendToChild", "UiSendToFirstLevelChild", "UiAssign", "UiConvertSxedio",
        "UiConvertSxedioSignReturnToCreator", "UiSignatureVerifiersNotification", "UiNotify", "UiInform", "UiSendToSign",
        "UiDiavgeiaPublish", "UiTaskAssignment", "UiUnAssign", "UiValidateTaskAssignment",
    ];

    public async Task<Result> Get(int documentId, int groupId, CancellationToken ct)
    {
        var res = await v1.Json(HttpMethod.Post, "services/document/get/formobile",
            new { Id = documentId, UserId = user.Required.Id, GroupId = groupId }, ct);
        var error = res?["Error"]?["ErrorMessage"]?.GetValue<string>();
        var doc = res?["Document"];
        if (doc is null) return new Result(false, error, []);
        var actions = (doc["DocumentActions"]?.AsArray() ?? [])
            .Where(a => a is not null)
            .Select(a =>
            {
                var code = a!["Code"]?.GetValue<string>() ?? "";
                var enabled = a["CanExecute"]?.GetValue<bool>() ?? false;
                return new DocumentAction
                {
                    Code = code,
                    Name = a["Name"]?.GetValue<string>() ?? code,
                    Enabled = enabled,
                    Reason = enabled ? null : Clean(a["ErrorMessage"]?.GetValue<string>()),
                    Mass = Mass.Contains(code) ? true : null,
                };
            })
            .Where(a => a.Code.Length > 0)
            .ToList();
        return new Result(true, null, actions);
    }

    public async Task<bool> CanSee(int documentId, int groupId, CancellationToken ct) => (await Get(documentId, groupId, ct)).CanSee;

    private static readonly Regex ExceptionType = new(@"[A-Za-z_][A-Za-z0-9_.]*Exception: ", RegexOptions.Compiled);

    /// <summary>
    /// The user-facing part of a CanExecute failure: v1 appends `exception.ToString()` (type and stack)
    /// to the message of exceptions other than WorkflowException.
    /// </summary>
    internal static string? Clean(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var line = message.Split('\n')[0].TrimEnd('\r');
        var m = ExceptionType.Match(line);
        if (m.Success && m.Index > 0) line = line[..m.Index];
        else if (m.Success) line = line[(m.Index + m.Length)..];
        line = line.Trim();
        return line.Length == 0 ? null : line;
    }
}
