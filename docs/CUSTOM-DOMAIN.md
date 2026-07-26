# Eigen domein voor de Admin GUI

De Admin GUI is na een verse installatie bereikbaar op een automatisch gegenereerde URL van de vorm
`[swa-url].azurestaticapps.net`. Wil je een eigen subdomein gebruiken — bijvoorbeeld
`wz.[club-domein]` — dan kan dat, en het kost niets.

## Kosten: geen

De Static Web Apps **Free tier** ondersteunt **2 custom domains per app**, inclusief **gratis,
automatisch vernieuwende SSL/TLS-certificaten**. Geen tier-upgrade nodig.

Bron: [Azure Static Web Apps hosting plans](https://learn.microsoft.com/azure/static-web-apps/plans)

> Loop je tegen de limiet van 2 aan, dan is de Standard tier **niet** gratis. Dat valt buiten het
> kostenbeleid van dit project — zie het kostenbeleid in de root-`CLAUDE.md`.

## Alleen een DNS-record is niet genoeg

Dit is de belangrijkste les van deze handleiding. Er zijn **drie** wijzigingen nodig, en twee ervan
geven een verwarrend foutbeeld als je ze overslaat:

| # | Wijziging | Waar | Symptoom als je het vergeet |
|---|---|---|---|
| 1 | Domein registreren | Static Web App | Certificaatfout; Azure serveert het domein niet |
| 2 | CORS-origin toevoegen | Function App | UI laadt wél, maar de browser blokkeert **elke** API-call → overal lege schermen |
| 3 | SPA redirect-URI toevoegen | Entra App Registration | Login mislukt met `AADSTS50011` (redirect URI mismatch) |

Symptoom 2 is het meest misleidend: de site lijkt te werken, maar toont nergens gegevens. In de
browserconsole staat dan een CORS-fout.

> **Waarschuwing bij stap 3.** De lijst met redirect-URI's moet worden **uitgebreid**, niet
> overschreven. Een `az ad app update --set spa.redirectUris=...` met alleen de nieuwe URI wist de
> bestaande — en dan kan niemand meer inloggen, ook niet op de oude URL. Gebruik daarom het script
> hieronder; dat leest de bestaande lijst, voegt toe en controleert daarna of er niets verdwenen is.

## Stap 1 — DNS-record aanmaken

Maak bij je DNS-provider een **CNAME**-record aan:

| Instelling | Waarde |
|---|---|
| Type | `CNAME` |
| Host / naam | het subdomein, bijvoorbeeld `wz` |
| Waarde / target | de gegenereerde hostnaam van je Static Web App (`[swa-url].azurestaticapps.net`) |
| TTL | standaardwaarde laten staan |

Je vindt de hostnaam met:

```powershell
az staticwebapp show --name <swa-naam> --resource-group <swa-rg> --query defaultHostname -o tsv
```

DNS-propagatie duurt afhankelijk van de TTL enkele minuten tot enkele uren. Azure kan het domein
pas valideren als het record wereldwijd resolvet.

### Apex-domein (zonder subdomein)

Wil je het domein zonder subdomein gebruiken (`[club-domein]` in plaats van `wz.[club-domein]`),
dan werkt een CNAME niet — DNS staat geen CNAME toe op de apex. Je hebt dan ALIAS/ANAME-records
nodig, of Azure DNS. Zie
[een apex-domein instellen](https://learn.microsoft.com/azure/static-web-apps/apex-domain-external).
Het script hieronder waarschuwt als je een apex-domein meegeeft.

## Stap 2 — De drie Azure-wijzigingen doorvoeren

Gebruik het script. Het is idempotent: een tweede run doet niets en meldt per stap `(al correct)`.

Doe **eerst** een dry-run:

```powershell
az login   # eenmalig, op het juiste account

.\scripts\azure\Add-CustomDomain.ps1 `
    -Domain 'wz.[club-domein]' `
    -StaticWebAppName '<swa-naam>' -StaticWebAppResourceGroup '<swa-rg>' `
    -FunctionAppName '<func-naam>' -FunctionAppResourceGroup '<func-rg>' `
    -ClientId '<app-id>' -ExpectedTenantId '<tenant-id>' `
    -WhatIf
```

Ziet het resultaat goed uit? Draai dan dezelfde regel **zonder** `-WhatIf`.

Het script stopt direct als je op de verkeerde tenant bent ingelogd, als een resource niet bestaat,
of als het domeinformaat ongeldig is. Resolveert het CNAME-record nog niet, dan waarschuwt het en
gaat het door — de SWA-validatie kan dan later alsnog slagen.

### Waar vind ik de waarden?

| Parameter | Vindplaats |
|---|---|
| `StaticWebAppName` / `StaticWebAppResourceGroup` | `az staticwebapp list -o table` |
| `FunctionAppName` / `FunctionAppResourceGroup` | `az functionapp list -o table` |
| `ClientId` | Azure Portal › App registrations › jouw app › Overview |
| `ExpectedTenantId` | Azure Portal › Microsoft Entra ID › Overview › Tenant ID |

## Stap 3 — Verifiëren

Validatie en certificaatuitgifte duren enkele minuten tot enkele uren. Controleer de status met:

```powershell
az staticwebapp hostname list --name <swa-naam> --resource-group <swa-rg> -o table
```

Loop daarna deze vier punten na — punt 3 is de enige die bewijst dat CORS klopt:

1. `https://wz.[club-domein]` opent **met een geldig certificaat**, zonder browserwaarschuwing.
2. Inloggen via Microsoft werkt, zonder `AADSTS50011`.
3. Een pagina die gegevens toont, laadt die gegevens ook echt. Lege schermen betekenen een
   CORS-probleem — controleer de browserconsole (F12).
4. De oude `[swa-url].azurestaticapps.net` werkt nog steeds. Beide URL's blijven geldig.

> Doe punt 2 in een **verse incognito-sessie**. MSAL bewaart het ID-token in `localStorage`; zonder
> verse sessie test je de oude token en niet de nieuwe redirect-URI.

## Wat níet hoeft te wijzigen

- **De Content-Security-Policy** in `staticwebapp.config.json`. Die staat op
  `connect-src 'self' https://login.microsoftonline.com {{AZURE_FUNCTIONAPP_URL}}` — `'self'` wordt
  automatisch je nieuwe domein, en de Function App staat er expliciet in.
- **`appsettings.Production.template.json`.** Daar staat geen frontend-URL in; MSAL leidt de
  redirect-URI af van de origin waarop de app draait.
- **De CI/CD-pipeline.** Het domein is runtime-configuratie in Azure; de build hoeft het niet te
  kennen.

## Club-specifieke waarden horen niet in git

Het domein, de SWA-hostnaam en de resourcenamen zijn club-identificerend en horen **nergens** in
de repository, ook niet in issues, PR-teksten of commit-messages. Ze gaan uitsluitend als parameter
mee bij het uitvoeren van het script.

Twee praktische punten:

- Gebruik **geen GitHub *Variable*** voor zo'n waarde als een workflow hem zou kunnen echoën:
  variables worden **niet gemaskeerd** in Actions-logs, en die logs zijn openbaar bij een publieke
  repository. Secrets worden wél gemaskeerd. In de opzet hierboven hoeft CI het domein niet te
  kennen, wat de veiligste variant is.
- Zet je club-specifieke waarden in `.githooks/sensitive-patterns.txt` (lokaal, staat in
  `.gitignore`). De git-hooks blokkeren dan een commit of push die zo'n waarde bevat. Zie de
  security-setup in de root-`CLAUDE.md`.

> Let op: een hostnaam blijft niet geheim. Zodra er een publiek vertrouwd certificaat voor wordt
> uitgegeven, verschijnt hij in de openbare
> [Certificate Transparency](https://certificate.transparency.dev/)-logs. Het doel van bovenstaande
> regels is dus niet geheimhouding, maar dat de repository club-neutraal blijft en een fork geen
> verwijzingen naar een andere club bevat.

## Verwante documentatie

- `SETUP-NIEUWE-CLUB.md` — volledige installatie voor een nieuwe club
- `docs/ENTRA-AUTH-BEHEER.md` — Entra-configuratie en de verplichte 3-user-test
- `docs/ARCHITECTURE.md` — architectuuroverzicht, inclusief de auth-lagen
