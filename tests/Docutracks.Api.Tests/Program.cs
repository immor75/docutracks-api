using System.Text.Json;
using Docutracks.Api.Documents;

// Console tests (nuget.org is not reachable for xunit): `dotnet run --project tests/Docutracks.Api.Tests`.
var failed = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}{(ok || detail is null ? "" : " — " + detail)}");
    if (!ok) failed++;
}
Dictionary<string, JsonElement> V(object o) =>
    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;
string J(List<string>? l) => l is null ? "(none)" : string.Join("|", l);
const string P = "ActionContext.Current.Parameters.";

/* ---------- actions ---------- */
{
    var f = ActionsService.ActionFields("UiAssign", V(new
    {
        Assignee = new[] { new { UserId = 11, GroupId = 1 } },
        ExtraAssignees = new[] { new { UserId = 12, GroupId = 1 }, new { UserId = 13, GroupId = 4 } },
        Urgent = true, ForAnswer = false, Comment = "Για τις ενέργειές σας.",
    }), DocumentType.Incoming, 5).Fields;
    Check("UiAssign: receiver as userId-groupId", J(f.GetValueOrDefault(P + "Kinisi.UserReceivers.Id")) == "11-1");
    Check("UiAssign: co-assignees", J(f.GetValueOrDefault(P + "Kinisi.SecondaryChargeReceivers.Id")) == "12-1|13-4");
    Check("UiAssign: urgent posted, for-answer not", J(f.GetValueOrDefault(P + "Kinisi.UrgentFlags")) == "true" && !f.ContainsKey(P + "Kinisi.IsForAnswerFlags"));
    Check("UiAssign: kind SEND (hidden input)", J(f.GetValueOrDefault(P + "Kinisi.Kinds.Id")) == "2");
    Check("UiAssign: comment", J(f.GetValueOrDefault(P + "Comment.Comment")) == "Για τις ενέργειές σας.");
}
{
    var f = ActionsService.ActionFields("UiSend", V(new
    {
        Groups = new object[] { new { GroupId = 41, EnergeiaTypeId = 1, Urgent = true }, new { GroupId = 7, EnergeiaTypeId = 6 } },
        Comment = "x",
    }), DocumentType.Outgoing, 5).Fields;
    Check("UiSend: one row per group, aligned",
        J(f[P + "Kinisi.GroupReceivers.Id"]) == "41|7" && J(f[P + "Kinisi.EnergeiaTypes.Id"]) == "1|6"
        && J(f[P + "Kinisi.UrgentFlags"]) == "true|false" && J(f[P + "Kinisi.Kinds.Id"]) == "2|2" && J(f[P + "Kinisi.Comments"]) == "x|x",
        string.Join("; ", f.Select(kv => kv.Key + "=" + J(kv.Value))));
}
{
    var draft = ActionsService.ActionFields("UiReturn", V(new { SignatureId = 0, Comment = "διόρθωση" }), DocumentType.Draft, 77).Fields;
    Check("UiReturn draft: to the author, energeia NONE, signature",
        J(draft[P + "Kinisi.UserReceiver.Id"]) == "77" && J(draft[P + "Kinisi.EnergeiaTypes.Id"]) == "5"
        && J(draft[P + "SignerToReturnDraft.SignatureId"]) == "0" && J(draft[P + "Kinisi.Comments"]) == "διόρθωση");
    var incoming = ActionsService.ActionFields("UiReturn", V(new { EnergeiaTypeId = 9 }), DocumentType.Incoming, 77).Fields;
    Check("UiReturn incoming: chosen energeia, no signature", J(incoming[P + "Kinisi.EnergeiaTypes.Id"]) == "9" && !incoming.ContainsKey(P + "SignerToReturnDraft.SignatureId"));
}
{
    var f = ActionsService.ActionFields("UiArchive", V(new { ThematicFolders = new object[] { new { Id = 3, Name = "Α, Β" }, new { Id = (int?)null, Name = "Νέος" } } }), 1, 5).Fields;
    Check("UiArchive: folders Name/Id aligned, new = 0",
        J(f["CurrentDocument.CurrentCopy.DocumentFolders.Name"]) == "Α, Β|Νέος" && J(f["CurrentDocument.CurrentCopy.DocumentFolders.Id"]) == "3|0");
}
{
    var f = ActionsService.ActionFields("UiChangeCreator", V(new { CreatedBy = new[] { new { UserId = 8, GroupId = 3 } }, CreatedForGroup = new[] { new { GroupId = 9 } } }), 2, 5).Fields;
    Check("UiChangeCreator: user, its group, issuing group",
        J(f[P + "CreatedBy.CreatedBy"]) == "8" && J(f[P + "CreatedByGroup.CreatedByGroup"]) == "3" && J(f[P + "CreatedForGroup.CreatedForGroup"]) == "9");
}

/* ---------- register ---------- */
{
    var form = new RegisterForm { NewType = 1, DocumentType = DocumentType.Incoming };
    var r = RegisterService.Fields(form, V(new
    {
        Title = "Αίτηση", SaveTitle = true, KindId = 3, KindData = new { afm = "123456789" },
        MainFile = new[] { new { TempId = "aa", Name = "a.pdf" } },
        Attachments = new object[] { new { FileId = 55, Name = "b.pdf" }, new { TempId = "bb", Name = "c.pdf" } },
        Sender = new[] { new { Id = (int?)null, Name = "Νέος Αποστολέας", Email = "x@y.gr" } },
        OtherRecipients = new object[] { new { Id = 15, Name = "Υπάρχων" }, new { Id = (int?)null, Name = "Δεύτερος" } },
        SendDate = "2026-10-20", SenderProtocol = "1234/2026", ProtocolBook = 0,
        Recipients = new object[] { new { GroupId = 41 }, new { UserId = 11, GroupId = 2 } },
        Categories = new[] { 4, 5 }, Related = new[] { new { Id = 900, Title = "x" } }, Transactors = Array.Empty<object>(),
    }), 2, old: false, replyTo: null);
    var f = r.Fields;
    Check("register: hidden inputs of the editor", J(f["CurrentDocument.Type.Id"]) == "1" && J(f["AppContext.DocNewTypes.Id"]) == "1" && J(f["DocOrgGroupId"]) == "2");
    Check("register: files kept by Id, new by TempId", J(f["CurrentDocument.MainFile.Id.new"]) == "aa" && J(f["CurrentDocument.Attachments.Id"]) == "55" && J(f["CurrentDocument.Attachments.Id.new"]) == "bb");
    Check("register: new contacts as placeholders, aligned e-mails",
        J(f["CurrentDocument.Apostoleas.Id"]) == "new:0" && J(f["CurrentDocument.Apostoleas.Email"]) == "x@y.gr"
        && J(f["CurrentDocument.ContactRecipients.Id"]) == "15|new:1" && J(f["CurrentDocument.ContactRecipients.Email"]) == "|"
        && r.NewContacts.Count == 2 && r.NewContacts[1].Name == "Δεύτερος");
    Check("register: dates as dd/MM/yyyy", J(f["CurrentDocument.DateApostolis"]) == "20/10/2026");
    Check("register: recipients group or user-group", J(f["CurrentDocument.Recipients.Id"]) == "41|11-2");
    Check("register: kind and its data", J(f["CurrentDocument.Kind.Id"]) == "3" && f["CurrentDocument.DocumentKindMetadataInstance"][0].Contains("123456789"));
    Check("register: related with relation «other»", J(f["CurrentDocument.Related.Document.Id"]) == "900" && J(f["CurrentDocument.Related.Relation.Id"]) == "6");
    Check("register: transactors of an incoming", f.ContainsKey("CurrentDocument.ContactTransactorsIncoming") == false && !f.ContainsKey("CurrentDocument.ContactTransactors"));
    Check("register: save title", J(f["CurrentDocument.ShouldSaveCurrentTitleAsPredefined"]) == "true");
}
{
    var form = new RegisterForm { NewType = 6, DocumentType = DocumentType.Draft };
    var f = RegisterService.Fields(form, V(new
    {
        Title = "Σχέδιο", FromTemplate = true, MainFile = new[] { new { TempId = "aa" } },
        FinalSigners = new[] { new { GroupId = 41 } }, ExtraCosigners = new[] { new { GroupId = 7 } },
        CoAuthors = new[] { new { UserId = 3, GroupId = 2 } }, Author = new[] { new { UserId = 5, GroupId = 2 } },
        CreatedForGroup = new[] { new { GroupId = 2 } }, Related = Array.Empty<object>(),
    }), 2, old: false, replyTo: 900).Fields;
    Check("draft: template instead of main file", J(f["CurrentDocument.CreateMainFileFromTemplate"]) == "true" && !f.ContainsKey("CurrentDocument.MainFile.Id.new"));
    Check("draft: picked signers", J(f["CurrentDocument.Signatures.ToSign.Id"]) == "41" && J(f["CurrentDocument.Cosignatures.ToSign.Id"]) == "7"
        && J(f["CurrentDocument.CoAuthorsWithSignature.Signator.Id"]) == "3-2");
    Check("draft: author, issuing group", J(f["CurrentDocument.Author.Id"]) == "5" && J(f["CurrentDocument.CreatedForGroup.Id"]) == "2");
    Check("answer: related as reply even when removed", J(f["CurrentDocument.Related.Document.Id"]) == "900" && J(f["CurrentDocument.Related.Relation.Id"]) == "1");
}
{
    var form = new RegisterForm { NewType = 1, DocumentType = DocumentType.Incoming };
    var f = RegisterService.Fields(form, V(new { OldProtocolNumber = "12/2019", OldProtocolDate = "2019-03-01" }), 2, old: true, replyTo: null).Fields;
    Check("old document: global protocol", J(f["CurrentDocument.GlobalProtocolText"]) == "12/2019" && J(f["CurrentDocument.GlobalProtocolDate"]) == "01/03/2019"
        && J(f["CurrentDocument.IsOldProtocol"]) == "true");
}

/* ---------- answers of the classic UI ---------- */
{
    var ok = Docutracks.Api.Infrastructure.ClassicClient.Redirected(
        "/el/Document/2/UiViewDoc/104480?aCtx=%7BCode%3A%27UiNew%27%2CExecuteResult%3A%7BEntityId%3A%27104480%27%2CSuccess%3Atrue%2CSkipSuccessMessage%3Afalse%2CMessage%3A%27%27%2CUiMessage%3A%27%CE%9F%CE%9A%27%7D%7D");
    Check("redirect: success, new document id, message", ok.Success && ok.EntityId == 104480 && ok.Message == "ΟΚ", $"{ok}");
    var plain = Docutracks.Api.Infrastructure.ClassicClient.Redirected("/el/Document/2/UiViewDoc?Id=55");
    Check("redirect: id from the query", plain.Success && plain.EntityId == 55);
    var html = "<div class='uiaction-messages ' style='display:none'><div class='uiaction-message'><h2>Αποτυχία</h2><div class='errormessage'>Δεν έχετε <b>δικαίωμα</b> &amp; ρόλο.</div></div></div>";
    Check("failure: message of .errormessage", Docutracks.Api.Infrastructure.ClassicClient.ErrorMessage(html) == "Δεν έχετε δικαίωμα & ρόλο.");
    Check("token: hidden input in any attribute order",
        Docutracks.Api.Infrastructure.ClassicClient.TokenInput("<form><input value=\"abc+/=\" type=\"hidden\" name=\"__RequestVerificationToken\" /></form>") == "abc+/=");
    Check("cookie: anti-forgery cookie replaces one of the same name",
        Docutracks.Api.Infrastructure.ClassicClient.Merge(".ASPXAUTH=x; __RequestVerificationToken=old", "__RequestVerificationToken=new") == ".ASPXAUTH=x; __RequestVerificationToken=new");
    var diagram = """
        <div id="authSignCalcList"><div>Τμήμα / Χρήστης<input type="hidden" name="CurrentDocument.CoAuthorsWithSignature.Signator.Id" value="5-2&amp;Calculated"/></div></div>
        <div id="usersignatures"><input type="hidden" name="CurrentDocument.Signatures.ToSign.Id" value="41" /></div>
        <div id="endorSignCalcList"><div>Δ/νση<input type="hidden" name="CurrentDocument.Cosignatures.ToSign.Id" value="7&Calculated" /></div></div>
        <div id="finalSignersCalcList"><div>Τμήμα<input type="hidden" name="CurrentDocument.Signatures.ToSign.Id" value="41&Calculated" /></div></div>
        """;
    var inputs = ClassicWriter.ChainInputs(diagram);
    Check("chain: only the calculated inputs, in order", string.Join("|", inputs.Select(i => i.Value)) == "5-2&Calculated|7&Calculated|41&Calculated",
        string.Join("|", inputs.Select(i => i.Value)));
    var steps = RegisterService.ParseChain(inputs);
    Check("chain: steps with type, user and group",
        steps.Count == 3 && steps[0].Type == 3 && steps[0].UserId == 5 && steps[0].GroupId == 2 && steps[1].Type == 2 && steps[1].GroupId == 7 && steps[2].Type == 1);
}

/* ---------- v1 messages ---------- */
Check("Clean: type and stack removed", V1Mobile.Clean("Δεν επιτρέπεται. System.Exception: x\r\n   at A.B()") == "Δεν επιτρέπεται.");
Check("Clean: empty", V1Mobile.Clean("  ") is null);

Console.WriteLine(failed == 0 ? "All passed." : $"{failed} failed.");
return failed == 0 ? 0 : 1;
