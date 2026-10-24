# Docutracks API v2: αρχιτεκτονική

Ένα ενιαίο API για Έγγραφα και Υποθέσεις, που χρησιμοποιεί το νέο Angular UI στη θέση των κλήσεων προς το υπάρχον `/services`. Ζει εκτός του solution του Docutracks και εγκαθίσταται δίπλα του στον ίδιο IIS.

## Αποφάσεις

| Θέμα | Απόφαση |
| --- | --- |
| Πλατφόρμα | ASP.NET Core 8 (minimal APIs), ξεχωριστό repo, εκτός solution. **Καμία αλλαγή στο solution του Docutracks.** |
| Θέση | IIS εφαρμογή `/api` στο ίδιο site με το Docutracks: `https://<host>/api/v2/...`. Ίδιο domain, άρα ο browser στέλνει τα ίδια cookies· δεν χρειάζεται CORS. |
| Δεν πειράζει | Το `/services` (v1) μένει όπως είναι για Razor, κινητά και διασυνδέσεις. Τα υπάρχοντα `/services/v2/folders/*` του FoldersModule μένουν και αυτά. |
| Ταυτότητα | Κάθε αίτημα: τα cookies και το `Authorization` του browser προωθούνται στο v1 `GET /services/authentication/currentUserAsLogin` (cache 60 s ανά διαπιστευτήρια). Ο ρόλος στην υπηρεσία `GroupId` ελέγχεται με SQL. Το `UserId` δεν διαβάζεται ποτέ από τον client. |
| Αναγνώσεις | SQL απευθείας (μικρός mapper πάνω στο `DbConnection`, login μόνο για ανάγνωση) για ό,τι δεν έχει κανόνες: πεδία εγγράφου, αρχεία, ιστορικό, ταξινομήσεις, ρυθμίσεις. |
| Λογική ροών | Λίστα από το v1 `documents/documentslistsearch` (ίδιο `ListModel` με τη λίστα του Razor)· ορατότητα εγγράφου και διαθεσιμότητα ενεργειών από το v1 `document/get/formobile` (`CanExecute`). |
| Εγγραφές | Υπογραφή μέσω v1 `sign/remote/document`. Οι υπόλοιπες **στέλνουν τη φόρμα του κλασικού UI** (Razor) ως ο χρήστης, όπως ο browser όταν πατιέται το κουμπί: ίδιο URL, πεδία, αρχεία, cookies και anti-forgery token. Ένα αίτημα ανά έγγραφο στις μαζικές ενέργειες. Όλες περνούν από την `ClassicWriter`, ώστε να αντικατασταθεί από typed υπηρεσίες του v1 όταν καταργηθεί το Razor. |

## Ροή ενός αιτήματος

```
browser ──/api/v2/documents/...──► .NET 8 service
                                     ├─ ταυτότητα: v1 currentUserAsLogin (cache)
                                     ├─ ανάγνωση: SQL (read-only)
                                     ├─ λογική ροών: v1 /services/document/... (με τα cookies του χρήστη)
                                     └─ εγγραφές: v1 ή οι φόρμες του Razor /el/Document/{υπηρεσία}/... (με τα cookies του χρήστη)
```

Οι κλήσεις προς το v1 και τις φόρμες γίνονται **ως ο χρήστης**, οπότε τα δικαιώματα ελέγχονται από τον ίδιο κώδικα με σήμερα.

## Endpoints και πηγή

Συμβόλαια: `docs/documents-api.md` του `docutracks-angular-ui` (αυτούσια). Βάση `/api/v2/documents/`.

| Endpoint | Πηγή |
| --- | --- |
| `GET filters` | SQL (ρυθμίσεις, ταξινομήσεις, ρόλοι) + σταθερός κατάλογος φακέλων όπως `ListModel.FolderTypes` |
| `POST search` | v1 `documents/documentslistsearch` (`ForList`): φάκελος, ορατότητα, κριτήρια, σελίδα· SQL μόνο για «επόμενος υπογράφων» |
| `GET {id}` | SQL (έγγραφο, αντίγραφο υπηρεσίας, επαφές, υπογραφές, σχετικά, αρχεία) |
| `GET {id}/history` | SQL (κινήσεις, ιστορικό ενεργειών) |
| `GET {id}/files` | αρχεία από v1 `file/get/{id}`, ZIP στο service |
| `GET /services/file/get/{id}` | μένει στο v1 (ο client το καλεί ήδη) |
| `POST actions` | v1 `document/get/formobile` ανά έγγραφο (`CanExecute`)· `Mass` = `UIActionsHelper._availableMassiveUiActions` |
| `POST flags` | `Document/{υπηρεσία}/UpdateDocumentReadyStatus`, `UpdateDocumentImportance` (JSON) |
| `POST export` | `List/{υπηρεσία}/RequestDocumentExport` ή `ExportDocumentsExtended` με `docIds` (ίδια εργασία Gateway με το Razor)· με φίλτρα, τα ids από το v1 `documentslistsearch` (έως 5.000) |
| `GET action-data/{code}` | περιγραφή φόρμας στο service· επιλογές από SQL και οργανόγραμμα v1 |
| `POST action` | φόρμα `UiViewDoc/{Code}/{id}` → POST `UiViewDoc/{id}` με `uiactionexec = Code` (πεδία του `UiActions/UiViewDoc/{Code}.cshtml`) |
| `GET thematic-folders` | SQL |
| `GET sign-data`, `POST sign` | SQL (μέσα υπογραφής του χρήστη)· v1 `sign/remote/document` ανά έγγραφο, με τα ίδια διαπιστευτήρια και OTP (όπως η μαζική υπογραφή του Razor) |
| `POST sign/otp` | ΣΗΔΕ: `Helper/RequestOtpForSDDDSign`· οι άλλοι πάροχοι δεν στέλνουν OTP κατά παραγγελία |
| `GET new-types`, `register-form`, `kinds`, `kinds/{id}/form`, `metadata-classes/{code}/form` | SQL (τύποι, ρόλοι, ρυθμίσεις, είδη, πρότυπα Formio) |
| `POST files` | προσωρινή αποθήκευση στο service (δίσκος, καθαρισμός μετά από 6 ώρες)· τα αρχεία ανεβαίνουν μαζί με τη φόρμα της καταχώρισης |
| `POST signature-chain` | POST `_Helpers/EndorsementCalculation?reset=true` (ό,τι κάνει ο «Υπολογισμός υπογραφών» του editor)· ονόματα από SQL |
| `POST register`, `GET {id}/edit-form`, `POST {id}/edit` | φόρμα από SQL· POST `UiNew/UiNew` ή `UiEdit/{id}` με τα πεδία του `DocumentPropertiesEditor`. Στα σχέδια προστίθεται η σειρά υπογραφών όπως το «signatureDiagram» της φόρμας |

Υποθέσεις: `/api/v2/folders/**` και `/api/v2/folder-types/**` προωθούν αυτούσια στα υπάρχοντα `/services/v2/folders/*` και `/services/v2/folder-types/*` του FoldersModule, ώστε ο client να έχει μία βάση.

## Εγγραφές: η φόρμα του κλασικού UI

Οι ενέργειες του Docutracks παίρνουν typed παραμέτρους (`IList<BaseUser>`, `Kinisi`, `ExtendedDocument`) που φτιάχνει ο `DocumentModelBinder` από τη φόρμα· το v1 `/document/action` με `Dictionary<string, object>` δεν αρκεί. Αντί να γραφτεί κώδικας στο solution, το API στέλνει την ίδια τη φόρμα:

1. **Πεδία.** Οι τιμές του API (π.χ. `Assignee: [{UserId, GroupId}]`) γίνονται τα ονόματα και οι τιμές που στέλνει η φόρμα (`ActionContext.Current.Parameters.Kinisi.UserReceivers.Id = "11-1"`): `RazorFields`, `ActionsService.ActionFields`, `RegisterService.Fields`.
2. **Αποστολή** (`ClassicClient`): `POST /{Classic:Culture}/Document/{υπηρεσία}/...` με τα cookies του browser. Το `__RequestVerificationToken` παίρνεται με ένα GET της σελίδας της φόρμας (ή άλλης σελίδας με φόρμα) και κρατιέται 20 λεπτά ανά χρήστη· αν απορριφθεί, ξαναπαίρνεται μία φορά.
3. **Αποτέλεσμα.** Επιτυχία: redirect του `RedirectToUiAction` (στο `aCtx` το `EntityId`, π.χ. το νέο έγγραφο, και το `UiMessage`). Αποτυχία: η σελίδα ξανά, με το μήνυμα στο `.uiaction-messages .errormessage`. Σελίδα σύνδεσης → 401.
4. **Αρχεία:** τα uploads του `POST files` (στο `Files:TempPath`) ανεβαίνουν ως multipart στα πεδία `*.Id.new`, μόνο αν ανήκουν στον χρήστη, και σβήνονται μετά την καταχώριση.
5. **Νέες επαφές:** v1 `contacts/getcontactid` (`AddSender`: υπάρχουσα επαφή με αυτό το όνομα ή νέα), πριν την αποστολή.
6. **Σχέδια:** πρώτα POST στο `_Helpers/EndorsementCalculation` με τα ίδια πεδία· τα κρυφά πεδία του διαγράμματος (`{id}&Calculated`) μπαίνουν στη φόρμα της καταχώρισης, όπως στον browser.

Έτσι όλοι οι κανόνες (ρυθμίσεις, περιορισμένη επεξεργασία, υπογραφές, διανομή) μένουν στον κώδικα του Docutracks. Το τίμημα: εξάρτηση από τα routes και από λίγα σημάδια του HTML (redirect, `errormessage`, το token, τα κρυφά πεδία του διαγράμματος), που ελέγχονται στα τεστ. **Αν καταργηθεί το Razor**, η `ClassicWriter` αντικαθίσταται από typed υπηρεσίες στο v1 (`WebServices`) που φτιάχνουν τις παραμέτρους χωρίς MVC· τα endpoints και ο client δεν αλλάζουν.

## Ρυθμίσεις (`appsettings.json`)

| Κλειδί | Περιγραφή |
| --- | --- |
| `ConnectionStrings:Docutracks` | SQL Server του Docutracks, login μόνο για ανάγνωση (`db_datareader`) |
| `V1:BaseUrl` | π.χ. `https://localhost/` (κλήσεις στο ίδιο μηχάνημα) |
| `V1:IgnoreCertificateErrors` | `true` μόνο για test με self-signed πιστοποιητικό |
| `Classic:Culture` | το πρόθεμα γλώσσας των σελίδων του Docman (`el`) |
| `Classic:TokenMinutes` | πόσο κρατιέται το anti-forgery token ενός χρήστη (20) |
| `Classic:NewContactTypeId` | τύπος νέων επαφών· 0 = ο πιο συχνός |
| `Files:TempPath`, `Files:MaxSizeMb` | προσωρινά αρχεία καταχώρισης |
| `PathBase` | `/api` |

## Τι δεν καλύπτει ακόμη

- Ταξινόμηση της λίστας μόνο με την προεπιλογή του v1· φίλτρα «Ιστορικό κινήσεων» και «Μεταδεδομένα».
- Υπογραφή με USB token και επιλογή θέσης υπογραφής (κλασικό UI)· Εξερχόμενο με `ForceSignedOutgoing`.
- Φάκελοι ΚΣΗΔΕ, DocuGate, ΦΕΑ / υπηρεσιακά σημειώματα στο μενού· κλάσεις μεταδεδομένων· οι υποθέσεις ενός εγγράφου στην προβολή του.
- Αποστολή σε εξωτερικούς αποδέκτες με e-mail, ανέβασμα συνημμένων στην επιστροφή σχεδίου.

## Τεστ

`dotnet run --project tests/Docutracks.Api.Tests`: η μετάφραση των τιμών στα πεδία των φορμών του Razor και η ανάγνωση των απαντήσεών του (redirect, μήνυμα σφάλματος, token, διάγραμμα υπογραφών). Χωρίς βάση και χωρίς v1· οι εγγραφές δοκιμάζονται σε test server (βλ. `docs/deploy.md`).
