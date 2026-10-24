# Docutracks API v2

API για Έγγραφα και Υποθέσεις του νέου Angular UI (`docutracks-angular-ui`). ASP.NET Core 8, εκτός του solution του Docutracks. Αρχιτεκτονική: `docs/architecture.md`. Εγκατάσταση σε server: `docs/deploy.md`.

## Τοπική εκτέλεση με τα δεδομένα του test-new

Το API τρέχει στο μηχάνημά σας και μιλά με το test-new: διαβάζει από τη βάση του και γράφει μέσω του Docutracks, ως ο χρήστης που συνδέθηκε στο Angular.

Χρειάζεται:

- .NET 8 SDK (ή Visual Studio 2022 17.8+).
- **Πρόσβαση στον SQL Server του test-new** από το μηχάνημά σας (VPN ή κανόνας firewall) και ένα login μόνο για ανάγνωση. Χωρίς αυτό το API ξεκινά, αλλά οι σελίδες εγγράφων αποτυγχάνουν· τότε η δοκιμή γίνεται μόνο με εγκατάσταση στον server.

Μία φορά, ο κωδικός της βάσης στα user secrets (εκτός repo):

```
cd src/Docutracks.Api
dotnet user-secrets set "ConnectionStrings:Docutracks" "Server=<sql του test-new>;Database=<βάση>;User Id=<login>;Password=<κωδικός>;TrustServerCertificate=True"
```

Στο Visual Studio: δεξί κλικ στο project → *Manage User Secrets* και το ίδιο JSON:
`{ "ConnectionStrings": { "Docutracks": "..." } }`.

Εκτέλεση: profile **test-new** (http://localhost:5266, `appsettings.Development.json`: `PathBase=/api`, `V1:BaseUrl=https://test-new.docutracks.eu/`).

```
dotnet run --project src/Docutracks.Api --launch-profile test-new
```

Έλεγχος: http://localhost:5266/api/health → `{"Status":"ok"}`.

Μετά, στο `docutracks-angular-ui`: `npm run start:test-new-api` (βλ. το README εκεί).

**Οι εγγραφές είναι πραγματικές** (χρέωση, αποστολή, καταχώριση…): μόνο σε έγγραφα δοκιμής, π.χ. της υπηρεσίας 2 «Test».

## Τεστ

```
dotnet run --project tests/Docutracks.Api.Tests
```

Χωρίς βάση και χωρίς δίκτυο: η μετάφραση των τιμών στα πεδία των φορμών του Docutracks και η ανάγνωση των απαντήσεών του.

## Build χωρίς nuget.org

Μόνο για περιβάλλοντα χωρίς πρόσβαση στο nuget.org: `dotnet build src/Docutracks.Api -p:Offline=true --source <τοπικό feed>`. Αφήνει έξω τον SQL client (δεν συνδέεται σε βάση).
