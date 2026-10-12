#!/usr/bin/env bash
# check-egressguard-dekking.test.sh (#1676)
#
# Bewijst dat check-egressguard-dekking.sh rood kan worden: elke test maakt tijdelijk een
# overtreding, eist exit 1 en herstelt daarna. Er wordt nooit iets gecommit.
#
# Draagbaarheid (#1155): bash 3.2 én bash 5.

set -uo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

GUARD="scripts/ci/check-egressguard-dekking.sh"
ALLOWLIST="scripts/ci/egressguard-allowlist.txt"
PROEF="FunctionApp/Admin/ProefEgressDekking1676.cs"
PROEF_PG="FunctionApp.Postgres/Admin/ProefEgressDekking1676.cs"
PROEF_EP="Planner.Endpoints/Admin/ProefEgressDekking1676.cs"
PROEF_SH="Planner.Shared/Infrastructure/ProefEgressDekking1676.cs"
PROEF_INF="FunctionApp/Infrastructure/ProefEgressDekking1676.cs"
SJP_SQL="FunctionApp/Sync/SyncJobProcessor.cs"
SJP_PG="FunctionApp.Postgres/Sync/SyncJobProcessor.cs"
TMP_B="$(mktemp)"; TMP_SQL="$(mktemp)"; TMP_PG="$(mktemp)"
cp "$ALLOWLIST" "$TMP_B"; cp "$SJP_SQL" "$TMP_SQL"; cp "$SJP_PG" "$TMP_PG"
opruimen() {
  rm -f "$PROEF" "$PROEF_PG" "$PROEF_EP" "$PROEF_SH" "$PROEF_INF"
  cp "$TMP_B" "$ALLOWLIST"; cp "$TMP_SQL" "$SJP_SQL"; cp "$TMP_PG" "$SJP_PG"
  rm -f "$TMP_B" "$TMP_SQL" "$TMP_PG"
}
trap opruimen EXIT

mislukt=0
verwacht() {  # verwacht <slagen|falen> <omschrijving>
  bash "$GUARD" >/dev/null 2>&1; rc=$?
  if [ "$1" = "slagen" ] && [ "$rc" -eq 0 ]; then echo "  ok — $2"
  elif [ "$1" = "falen" ] && [ "$rc" -eq 1 ]; then echo "  ok — $2"
  else echo "::error::TEST MISLUKT — $2 (exit $rc, verwacht $1)"; mislukt=$((mislukt + 1)); fi
}

verwacht slagen "schone werkboom is groen"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); }\n' > "$PROEF"
verwacht falen "HttpClient zonder EgressGuard (SQL Server-tier)"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); }\n' > "$PROEF_PG"
verwacht falen "HttpClient zonder EgressGuard (Postgres-tier)"
rm -f "$PROEF_PG"

printf 'namespace Proef;\npublic static class P { static void M(IHttpClientFactory f) { } }\n' > "$PROEF"
verwacht falen "IHttpClientFactory zonder EgressGuard"
rm -f "$PROEF"

# Een vermelding in commentaar is geen controle.
printf '// EgressGuard hoort hier\nnamespace Proef;\npublic static class P { static readonly HttpClient C = new(); }\n' > "$PROEF"
verwacht falen "EgressGuard alleen in commentaar"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); const string EgressGuard = "naam"; }\n' > "$PROEF"
verwacht falen "EgressGuard alleen als naam (geen aanroep)"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); static bool Ok() => EgressGuard.ExternalIntegrationsAllowed(); }\n' > "$PROEF"
verwacht slagen "HttpClient mét EgressGuard-aanroep"
rm -f "$PROEF"

# Valkuilen: een naam in een //-staart, nameof(...) of een string is geen aanroep.
printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); } // EgressGuard.ExternalIntegrationsAllowed()\n' > "$PROEF"
verwacht falen "EgressGuard alleen in een //-staart na code"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); const string N = nameof(EgressGuard.ExternalIntegrationsAllowed); }\n' > "$PROEF"
verwacht falen "EgressGuard alleen in nameof(...)"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); const string S = "EgressGuard.ExternalIntegrationsAllowed()"; }\n' > "$PROEF"
verwacht falen "EgressGuard alleen in een stringliteral"
rm -f "$PROEF"

# Een URL in een string kapt de regel niet af: de aanroep erachter telt.
printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); static bool Ok() => "https://voorbeeld.nl" != "" && EgressGuard.ExternalIntegrationsAllowed(); }\n' > "$PROEF"
verwacht slagen "aanroep na een URL-string op dezelfde regel telt"
rm -f "$PROEF"

# Niet-uitvoerbare code over meerdere regels, verbatim strings en #if-blokken.
printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new();\n/* uitgeschakeld:\n   bool Ok() => EgressGuard.ExternalIntegrationsAllowed();\n*/ }\n' > "$PROEF"
verwacht falen "EgressGuard alleen in een meerregelig blokcommentaar zonder * aan het regelbegin"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new();\nconst string R = """\n  EgressGuard.ExternalIntegrationsAllowed()\n  """; }\n' > "$PROEF"
verwacht falen "EgressGuard alleen in een raw string over meerdere regels"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); const string Pad = @"c:\\"; const string S = "EgressGuard.ExternalIntegrationsAllowed()"; }\n' > "$PROEF"
verwacht falen "verbatim string die op een backslash eindigt, daarna de naam in een string"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new();\n#if NOOIT\n  static bool Ok() => EgressGuard.ExternalIntegrationsAllowed();\n#endif\n}\n' > "$PROEF"
verwacht falen "EgressGuard alleen binnen #if ... #endif"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); static string T() => $"{EgressGuard.ExternalIntegrationsAllowed()}"; }\n' > "$PROEF"
verwacht falen "EgressGuard alleen in een interpolatiegat (telt bewust niet)"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); static bool Ok() => EgressGuard.ExternalIntegrationsAllowedOoit(); }\n' > "$PROEF"
verwacht falen "andere naam met hetzelfde voorvoegsel is geen aanroep"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); const string R = """x"""; const string V = @"a""b\\"; static bool Ok() => EgressGuard.ExternalIntegrationsAllowed(); }\n' > "$PROEF"
verwacht slagen "aanroep na een raw en een verbatim string op dezelfde regel telt"
rm -f "$PROEF"

# Scope: Planner.Endpoints, Planner.Shared, Infrastructure/ en de handler-typen.
printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); }\n' > "$PROEF_EP"
verwacht falen "HttpClient zonder EgressGuard in Planner.Endpoints"
rm -f "$PROEF_EP"

printf 'namespace Proef;\npublic static class P { static readonly SocketsHttpHandler H = new(); }\n' > "$PROEF_SH"
verwacht falen "alleen SocketsHttpHandler zonder EgressGuard (Planner.Shared)"
rm -f "$PROEF_SH"

printf 'namespace Proef;\npublic static class P { static object H() => new HttpClientHandler(); }\n' > "$PROEF_INF"
verwacht falen "alleen HttpClientHandler zonder EgressGuard (FunctionApp/Infrastructure)"
rm -f "$PROEF_INF"

# Groot bestand: het token bovenaan mag niet wegvallen doordat grep vroeg stopt (SIGPIPE + pipefail).
{ printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); }\npublic static class Q {\n'
  i=0; while [ "$i" -lt 4000 ]; do printf '    static readonly int Opvulling%s = %s; // vult de pipebuffer\n' "$i" "$i"; i=$((i + 1)); done
  printf '}\n'; } > "$PROEF_SH"
verwacht falen "groot bestand met HttpClient bovenaan zonder EgressGuard"
rm -f "$PROEF_SH"

# Interpolated strings (#1676 ronde 3): een string ín een interpolatiegat sluit de omringende
# string niet af, een gat mag over meerdere regels lopen (C# 11), en een preprocessorregel opent
# geen commentaar.
printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); static string T(bool b) => $"{(b ? "EgressGuard.ExternalIntegrationsAllowed()" : "x")}"; }\n' > "$PROEF"
verwacht falen "EgressGuard alleen in een string binnen een interpolatiegat"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); static string T(bool b) => $@"{(b ? "EgressGuard.ExternalIntegrationsAllowed()" : "x")}"; }\n' > "$PROEF"
verwacht falen "EgressGuard alleen in een string binnen een verbatim interpolatiegat"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); static string T(int x) => $"a {\n  x } EgressGuard.ExternalIntegrationsAllowed() "; }\n' > "$PROEF"
verwacht falen "EgressGuard alleen in stringtekst na een interpolatiegat over meerdere regels"
rm -f "$PROEF"

printf 'namespace Proef;\n#region Endpoints /api/*\npublic static class P { static readonly HttpClient C = new(); }\n#endregion\n' > "$PROEF"
verwacht falen "HttpClient na een #region-regel met /* (geen blokcommentaar)"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly HttpClient C = new(); static bool Ok(bool b, System.DateTime t) => $"{(b ? "a" : "b")} {t:HH'"'"':'"'"'mm} {t:dd//MM} {{x}} {global::System.Math.Abs(1)}" != "" && $$"""{ {{b}} "q" }""" != "" && EgressGuard.ExternalIntegrationsAllowed(); }\n' > "$PROEF"
verwacht slagen "aanroep na interpolated strings met geneste strings, opmaak, {{ en raw \$\$ telt"
rm -f "$PROEF"

# Andere typen voor uitgaand verkeer en HttpClient-methoden zonder typenaam in het bestand.
printf 'namespace Proef;\npublic static class P { static string T() => new System.Net.WebClient().DownloadString("x"); }\n' > "$PROEF"
verwacht falen "WebClient zonder EgressGuard"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static object T() => System.Net.WebRequest.Create("x"); }\n' > "$PROEF"
verwacht falen "WebRequest zonder EgressGuard"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly System.Net.Sockets.TcpClient T = new(); }\n' > "$PROEF"
verwacht falen "TcpClient zonder EgressGuard"
rm -f "$PROEF"

printf 'namespace Proef;\npublic static class P { static readonly System.Net.Http.HttpMessageInvoker I = new(null!); }\n' > "$PROEF"
verwacht falen "HttpMessageInvoker zonder EgressGuard"
rm -f "$PROEF"

printf 'namespace Proef;\npublic partial class P { System.Threading.Tasks.Task<string> T() => _http.GetStringAsync("x"); }\n' > "$PROEF"
verwacht falen "GetStringAsync op een elders getypeerde client (partial class) zonder EgressGuard"
rm -f "$PROEF"

printf 'FunctionApp/Admin/BestaatNiet1676.cs  proefregel voor een bestand dat niet bestaat\n' >> "$ALLOWLIST"
verwacht falen "dode regel in de allowlist"
cp "$TMP_B" "$ALLOWLIST"

{ cat "$TMP_B"; printf 'FunctionApp/Admin/AdminSyncFunction.cs\n'; } > "$ALLOWLIST"
verwacht falen "allowlistregel zonder reden"
cp "$TMP_B" "$ALLOWLIST"

{ cat "$TMP_B"; grep -E '^FunctionApp/Sync/SportlinkSyncPipeline.cs ' "$TMP_B"; } > "$ALLOWLIST"
verwacht falen "dubbele allowlistregel"
cp "$TMP_B" "$ALLOWLIST"

# Volgorde in SyncJobProcessor.Run: zonder aanroep van de poortbeslissing vóór MarkRunningAsync rood.
sed 's/if (await BlokkeerBuitenProductieAsync(/if (await Uitgeschakeld(/' "$TMP_SQL" > "$SJP_SQL"
verwacht falen "SyncJobProcessor.Run (SQL Server) zonder poortbeslissing vóór MarkRunningAsync"
cp "$TMP_SQL" "$SJP_SQL"

sed 's/if (await OverslaanZonderUitgaandVerkeerAsync(/if (await Uitgeschakeld(/' "$TMP_PG" > "$SJP_PG"
verwacht falen "SyncJobProcessor.Run (Postgres) zonder poortbeslissing vóór MarkRunningAsync"
cp "$TMP_PG" "$SJP_PG"

# Alleen een echte aanroep ná de signatuur van Run telt: geen naam met voorvoegsel, geen
# definitie en geen aanroep in een methode boven Run.
sed 's/if (await OverslaanZonderUitgaandVerkeerAsync(/if (await NietOverslaanZonderUitgaandVerkeerAsync(/' "$TMP_PG" > "$SJP_PG"
verwacht falen "SyncJobProcessor.Run (Postgres) roept alleen een naam met hetzelfde achtervoegsel aan"
cp "$TMP_PG" "$SJP_PG"

sed 's/if (await BlokkeerBuitenProductieAsync(/if (await Uitgeschakeld(/' "$TMP_SQL" \
  | awk '/\[Function\("SyncJobProcessor"\)\]/ { print "    internal static Task<bool> BlokkeerBuitenProductieAsync(int x) => Task.FromResult(false);" } { print }' > "$SJP_SQL"
verwacht falen "SyncJobProcessor (SQL Server): alleen een definitie van de helper boven Run"
cp "$TMP_SQL" "$SJP_SQL"

sed 's/if (await BlokkeerBuitenProductieAsync(/if (await Uitgeschakeld(/' "$TMP_SQL" \
  | awk '/\[Function\("SyncJobProcessor"\)\]/ { print "    static async Task Elders() { if (await BlokkeerBuitenProductieAsync(null!, null!, null!, null!)) return; }" } { print }' > "$SJP_SQL"
verwacht falen "SyncJobProcessor (SQL Server): helperaanroep alleen in een methode boven Run"
cp "$TMP_SQL" "$SJP_SQL"

sed 's/if (await BlokkeerBuitenProductieAsync(/if (await Uitgeschakeld(/' "$TMP_SQL" \
  | awk '{ print } /if \(await Uitgeschakeld\(/ { print "        static Task<bool> BlokkeerBuitenProductieAsync(int x) => Task.FromResult(false);" }' > "$SJP_SQL"
verwacht falen "SyncJobProcessor.Run (SQL Server): alleen een lokale definitie van de helper vóór MarkRunningAsync"
cp "$TMP_SQL" "$SJP_SQL"

verwacht slagen "hersteld: schone werkboom is weer groen"

[ "$mislukt" -eq 0 ] || { echo "$mislukt test(s) mislukt."; exit 1; }
echo "OK — alle negatieve tests van de EgressGuard-dekkingsguard slaagden."
