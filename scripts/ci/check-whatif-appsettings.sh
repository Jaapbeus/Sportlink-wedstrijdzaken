#!/usr/bin/env bash
# check-whatif-appsettings.sh (#1455)
#
# What-if-poort voor infrastructure.yml. Een ARM PUT van een Function App met siteConfig.appSettings
# VERVANGT alle app settings; wat buiten bicep is beheerd verdwijnt stil. Dit script leest de JSON
# van `az deployment group what-if --result-format FullResourcePayloads --no-pretty-print -o json`
# en faalt (exit 1) als een bestaande app setting zou verdwijnen.
#
# Gebruik:
#   check-whatif-appsettings.sh --sites <whatif.json>               # namen van Web/sites in de changes
#   check-whatif-appsettings.sh <whatif.json> [<live-map>|<live-namen.json>]   # de poort
#   <live-map> bevat <sitenaam>.json per Function App (#1495); ontbrekend bestand = exit 2
#
# <live-namen.json>: JSON-array van objecten met "name" (uitvoer van
#   `az functionapp config appsettings list`), of van strings. Nodig omdat een site-GET in what-if
#   de appSettings meestal NIET in "before" meegeeft; zonder live-lijst is alleen "before" bekend.
#
# Fail-closed: onleesbare/onverwachte uitvoer = exit 2. Alleen NAMEN worden getoond, nooit waarden.
# Draagbaar: bash 3.2 + python3.
set -uo pipefail

if [ "${1:-}" = "--sites" ]; then mode=sites; shift; else mode=gate; fi
whatif="${1:-}"; live="${2:-}"
if [ -z "$whatif" ] || [ ! -f "$whatif" ]; then
    echo "FOUT: what-if-bestand ontbreekt" >&2; exit 2
fi

python3 - "$mode" "$whatif" "$live" <<'PY'
import json, sys

mode, whatif, live = sys.argv[1], sys.argv[2], sys.argv[3]

def fatal(msg):
    print("FOUT (poort faalt gesloten): " + msg, file=sys.stderr)
    sys.exit(2)

try:
    with open(whatif, encoding="utf-8") as f:
        data = json.load(f)
except Exception as e:
    fatal("what-if-uitvoer is geen geldige JSON (%s)" % type(e).__name__)

if not isinstance(data, dict) or not isinstance(data.get("changes"), list):
    fatal("what-if-uitvoer mist de lijst 'changes' - formaat onbekend")

def is_site(rid):
    parts = rid.lower().split("/providers/microsoft.web/sites/")
    return len(parts) == 2 and "/" not in parts[1]

sites = []
for c in data["changes"]:
    if not isinstance(c, dict) or not isinstance(c.get("resourceId"), str) or "changeType" not in c:
        fatal("change zonder resourceId/changeType - formaat onbekend")
    if is_site(c["resourceId"]):
        sites.append(c)

if mode == "sites":
    for c in sites:
        print(c["resourceId"].rsplit("/", 1)[1])
    sys.exit(0)

def names(settings, label):
    if not isinstance(settings, list):
        fatal("%s: appSettings is geen lijst" % label)
    out = set()
    for s in settings:
        if isinstance(s, dict) and isinstance(s.get("name"), str):
            out.add(s["name"])
        elif isinstance(s, str):
            out.add(s)
        else:
            fatal("%s: appSetting zonder 'name'" % label)
    return out

def app_settings(res):
    """None als het pad ontbreekt; anders de lijst."""
    if not isinstance(res, dict):
        return None
    sc = (res.get("properties") or {}).get("siteConfig")
    if not isinstance(sc, dict):
        return None
    return sc.get("appSettings")

def read_live(path):
    try:
        with open(path, encoding="utf-8") as f:
            return names(json.load(f), "live")
    except SystemExit:
        raise
    except Exception as e:
        fatal("live-appsettinglijst onleesbaar (%s)" % type(e).__name__)

# <live> is een map met <sitenaam>.json (per Function App, #1495) of een enkel bestand
# (oud gedrag: dezelfde lijst voor alle sites; alleen veilig bij precies een site).
import os
live_dir = live if (live and os.path.isdir(live)) else None
live_single = read_live(live) if (live and live_dir is None) else None

def live_for(site):
    if live_dir is not None:
        p = os.path.join(live_dir, site + ".json")
        if not os.path.isfile(p):
            return None
        return read_live(p)
    return live_single

failed = False
for c in sites:
    site = c["resourceId"].rsplit("/", 1)[1]
    ct = str(c["changeType"])
    if ct in ("NoEffect", "Ignore"):
        continue
    if ct == "Delete":
        print("FOUT: site '%s' zou worden verwijderd" % site); failed = True; continue
    if ct == "Create":
        continue
    after = app_settings(c.get("after"))
    if after is None:
        fatal("site '%s' (%s): geen after.properties.siteConfig.appSettings in what-if - kan niet bewijzen dat niets verdwijnt" % (site, ct))
    after_n = names(after, "after")
    before_raw = app_settings(c.get("before"))
    before_n = names(before_raw, "before") if before_raw is not None else set()
    live_names = live_for(site)
    if live_names is None and live_dir is not None:
        fatal("site '%s': geen live-lijst voor deze Function App - kan niet bewijzen dat niets verdwijnt" % site)
    if live_single is not None and len(sites) > 1:
        fatal("meerdere sites in what-if maar een enkele live-lijst - geef een map met <site>.json")
    if not before_n and live_names is None:
        fatal("site '%s': 'before' bevat geen appSettings en er is geen live-lijst meegegeven" % site)
    existing = before_n | (live_names or set())
    gone = sorted(existing - after_n)
    print("site '%s': bestaand=%d, na deploy=%d" % (site, len(existing), len(after_n)))
    if gone:
        failed = True
        print("FOUT: deze app settings zouden verdwijnen (alleen namen):")
        for n in gone:
            print("  - " + n)
    if len(after_n) < len(existing) and not gone:
        failed = True
        print("FOUT: aantal app settings daalt")

if failed:
    sys.exit(1)
print("OK: geen app settings die verdwijnen")
PY
