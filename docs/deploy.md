# Εγκατάσταση του API v2 (π.χ. στο test-new)

Δύο κομμάτια, στον ίδιο server και στο ίδιο IIS site με το Docutracks. **Το Docutracks δεν αλλάζει** (ούτε κώδικας ούτε `Web.config`):

1. **Docutracks API v2** (ASP.NET Core 8), IIS εφαρμογή `/api`.
2. **Angular UI** με βάση API `/api/v2` (production build).

Μέχρι να γίνει και το βήμα 2, το νέο UI συνεχίζει να καλεί το `/services/v2`, οπότε το βήμα 1 εγκαθίσταται χωρίς να επηρεάσει κανέναν.

Το API γράφει στέλνοντας τις φόρμες του κλασικού UI ως ο χρήστης (βλ. `docs/architecture.md`). Γι' αυτό χρειάζεται ο browser να έχει σύνδεση με cookie (`.ASPXAUTH`) στο ίδιο domain, όπως σήμερα.

## 0. Πριν ξεκινήσετε

- Hosting Bundle του **ASP.NET Core 8** στον server (Runtime + ASP.NET Core Module V2). Μετά την εγκατάσταση: `iisreset`.
- Ένας **φάκελος προσωρινών αρχείων** για τα uploads πριν την καταχώριση, π.χ. `D:\DocutracksApiTemp` (εκτός wwwroot).
- Ένα **SQL login μόνο για ανάγνωση** στη βάση του Docutracks (`db_datareader`), π.χ. `docutracks_api_ro`.

## 1. API v2 (ASP.NET Core 8)

### Build

Στο Visual Studio: `Docutracks.Api.sln` → δεξί κλικ στο `Docutracks.Api` → **Publish** → Folder, `Release`, `net8.0`, Deployment mode *Framework-dependent*, Target runtime *win-x64*. (Ή: `dotnet publish src/Docutracks.Api -c Release -r win-x64 --self-contained false -o publish`.)

Μην χρησιμοποιήσετε `-p:Offline=true` (αυτό είναι μόνο για builds χωρίς nuget.org και αφήνει έξω τον SQL client).

Έλεγχοι πριν την εγκατάσταση: `dotnet run --project tests/Docutracks.Api.Tests` → «All passed.»

### IIS

1. Αντιγραφή του φακέλου publish π.χ. στο `D:\inetpub\DocutracksApi`.
2. Application pool `DocutracksApi`: **No Managed Code**, Integrated, ApplicationPoolIdentity.
3. Στο site του Docutracks: Add Application → Alias `api`, pool `DocutracksApi`, physical path ο φάκελος του βήματος 1.
   Αν το Docutracks έχει ήδη κάτι στη διαδρομή `/api`, επιλέξτε άλλο alias και αλλάξτε `PathBase` και το `api-base.production.ts` του Angular.
4. `appsettings.Production.json` δίπλα στο `Docutracks.Api.dll` (πρότυπο: `appsettings.Production.sample.json`):

| Κλειδί | Τιμή |
| --- | --- |
| `PathBase` | `/api` (ίδιο με το alias) |
| `ConnectionStrings:Docutracks` | ο SQL Server του Docutracks με το login ανάγνωσης |
| `V1:BaseUrl` | η διεύθυνση του Docutracks όπως τη βλέπει ο server, π.χ. `https://test-new.docutracks.eu/`. Με πολλά sites στον IIS, το hostname του site και όχι `localhost` |
| `V1:IgnoreCertificateErrors` | `true` μόνο αν ο server δεν εμπιστεύεται το δικό του πιστοποιητικό (test) |
| `Classic:Culture` | το πρόθεμα γλώσσας των σελίδων του Docutracks (`el`, όπως στο `/el/Account/LogOn`) |
| `Files:TempPath` | ο φάκελος προσωρινών αρχείων |
| `Files:MaxSizeMb` | όριο αρχείου, όπως του Docutracks |

   Εναλλακτικά, ο κωδικός της βάσης ως μεταβλητή περιβάλλοντος του pool: `ConnectionStrings__Docutracks`.
5. Δικαιώματα: ο χρήστης του pool `DocutracksApi` (`IIS AppPool\DocutracksApi`) **Modify** στο `Files:TempPath`.
6. Το `web.config` του API κρατά μόνο το ASP.NET Core Module (`<handlers><clear/>`). Αν το `/api` δώσει 500.19 ή 500.21 επειδή κληρονομεί modules του Docutracks, χρειάζεται `<location path="." inheritInChildApplications="false">` γύρω από τα `system.web`/`system.webServer` του Web.config του Docutracks. Αυτή είναι η μόνη πιθανή αλλαγή στο Docutracks, και μόνο ρύθμιση.
7. `https://<host>/api/health` → `{"Status":"ok"}`. Με συνδεδεμένο χρήστη: `https://<host>/api/v2/documents/filters?GroupId=2` → `{"Success":true,...}`.

Logs: Event Viewer → Application (πηγή «IIS AspNetCore Module V2») ή `stdoutLogEnabled="true"` στο `web.config` προσωρινά.

### Πρώτες δοκιμές εγγραφών

Μόνο σε έγγραφα δοκιμής (π.χ. της υπηρεσίας 2 «Test»):

- Αναγνωσμένο/σημαντικό ενός εγγράφου.
- Μια ενέργεια με σχόλιο (π.χ. Αρχειοθέτηση και Επαναφορά).
- Χρέωση και Αποστολή, και έλεγχος στο ιστορικό του κλασικού UI.
- Νέο εισερχόμενο με αρχείο και νέο αποστολέα· νέο σχέδιο με τελικό υπογράφοντα (σειρά υπογραφών).
- Μια ενέργεια που δεν επιτρέπεται, ώστε να φανεί το μήνυμα του Docutracks.

Αν μια εγγραφή αποτύχει με «Η φόρμα του Docutracks δεν βρέθηκε» ή με μήνυμα για anti-forgery, το log του API γράφει ποια σελίδα ζητήθηκε.

## 2. Angular UI

Το production build καλεί το `/api/v2` (`src/app/core/api-base.production.ts`)· το development build και το mock μένουν στο `/services/v2`.

```
npm run build -- --configuration production
```

και αντιγραφή του `dist/` στη θέση του νέου UI (π.χ. `/apps-next`), όπως μέχρι τώρα.

## Ενημέρωση

API: νέο publish στον ίδιο φάκελο. Ο IIS κρατά τα αρχεία ανοιχτά· βάλτε πρώτα ένα `app_offline.htm` στον φάκελο (ή σταματήστε το pool), αντιγράψτε, σβήστε το `app_offline.htm`.

Μετά από αναβάθμιση του Docutracks: τα τεστ του API ελέγχουν τα σημεία του HTML που διαβάζει· αν άλλαξαν φόρμες ενεργειών ή το `DocumentPropertiesEditor`, ξανατρέξτε τις πρώτες δοκιμές εγγραφών.

## Επιστροφή στο προηγούμενο

Το Angular build με βάση `/services/v2` δεν καλεί το `/api`· σε αυτό λειτουργούν μόνο οι Υποθέσεις (τα `/services/v2/folders/*` του FoldersModule), αφού τα Έγγραφα του `/services/v2/documents` δεν υπάρχουν στο v1. Η εφαρμογή `/api` αφαιρείται από τον IIS χωρίς να επηρεάσει το Docutracks.
