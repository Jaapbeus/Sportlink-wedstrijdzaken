# AGENTS.md — FunctionApp/ (SQL Server-tier)

Geldt voor `FunctionApp/` en komt bovenop de root-`AGENTS.md`. **Lees `../AGENTS.md` als de rootregels niet in je context staan**: Claude Code laadt de root-import niet vanuit een submap zolang `hasClaudeMdExternalIncludesApproved` voor het project uit staat (een gebruikersinstelling, niet af te dwingen vanuit de repository); Codex leest beide bestanden samen.

- Dit is de **SQL Server-tier** (`net10.0`, isolated worker, namespace `SportlinkFunction`); productie draait op `FunctionApp.Postgres/`. Een feature landt op beide gebouwde tiers (root: Databasetiers).
- **`Pooling=false`** in `SystemUtilities.DatabaseConfig` (#808) nooit verwijderen: een gepoolde verbinding blijft na `Dispose()` als sessie open en blokkeert de auto-pause van de gratis database.
- **Log nooit een Sportlink-request-URL**: de `clientId` in de querystring is een publieke identifier (SECURITY.md) maar hoort niet in git, logs of telemetrie. Log het endpoint plus de `wedstrijdcode`; CI blokkeert een logtemplate met een URL-placeholder (#1200).
- **Sync**: `/programma` is de enige bron voor toekomstige wedstrijden. `/uitslagen` verrijkt alleen scorevelden en voegt alleen verleden wedstrijden toe: het overschrijft nooit gedeelde velden (wedstrijd, teams, veld, aanvangstijd) en voegt geen toekomstige wedstrijden toe. Handmatige sync `GET /api/sync-matches` vereist een Entra-Bearer-token met rol `admin` (#1350; de master key werkt niet meer).
- **Automatische Sportlink-login (#1411)**: een agent voert die nooit zelf uit; credentials en `SportlinkAutoLoginEncryptionKey` staan nooit in code, logs of tekst (egress-guard en menselijke live-testgrens, `SPORTLINK-AUTOLOGIN.md`).
- **Feedback (#764)**: `POST feedback/*` is open voor admin én user (`ExecuteAuthenticatedAsync`); de melder komt uitsluitend uit het Easy Auth-principal en staat nooit in het publieke issue (`FEEDBACK.md`).
- Tests: `dotnet test FunctionApp.Tests/FunctionApp.Tests.csproj`; lokale SQL Server via Docker (`docs/DEVELOPER-SETUP.md` §4.4).
- Uitwerking: `docs/SPORTLINK-DATASERVICE.md` (veldreferentie, nieuwe databron, debug-SQL) en `docs/API.md`.
