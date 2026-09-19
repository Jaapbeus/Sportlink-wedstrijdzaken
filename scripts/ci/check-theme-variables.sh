#!/usr/bin/env bash
# check-theme-variables.sh — bewaakt de thema-CSS-variabelen in BlazorAdmin/wwwroot/css/app.css
# (#1255, epic #1249).
#
# Waarom deze guard bestaat: een selector die var(--theme-iets) gebruikt terwijl die variabele
# nergens gedefinieerd is, geeft géén foutmelding. De eigenschap valt gewoon weg en het element
# rendert ongestyled — zichtbaar alleen als iemand precies die pagina in precies die modus opent.
# Sinds het thema twee modi heeft is het aantal variabelen verdrievoudigd, en elke nieuwe kleur
# moet op vier plekken staan (-light, -dark, de effectieve verwijzing in :root en die in
# [data-theme="dark"]). Eén vergeten plek is exact zo'n stille fout.
#
# Bash 3.2-compatibel (de standaard /bin/bash van macOS): geen declare -A, geen mapfile,
# geen ${var,,}. Zie de cross-platform-regels in CLAUDE.md.

set -euo pipefail

CSS="BlazorAdmin/wwwroot/css/app.css"
fouten=0

if [ ! -f "$CSS" ]; then
    echo "::error::$CSS niet gevonden."
    exit 1
fi

# Commentaarblokken eruit: die noemen variabelenamen ter uitleg en zijn geen definitie of gebruik.
zonder_commentaar="$(mktemp)"
trap 'rm -f "$zonder_commentaar"' EXIT
sed 's|/\*|\
&|g; s|\*/|&\
|g' "$CSS" | awk '
    /\/\*/ { inblok = 1 }
    inblok == 0 { print }
    /\*\// { inblok = 0 }
' > "$zonder_commentaar"

# Gedefinieerd = staat links van een dubbele punt.
gedefinieerd="$(grep -oE '^[[:space:]]*--theme-[a-z0-9-]+[[:space:]]*:' "$zonder_commentaar" \
    | tr -d ' \t:' | sort -u)"

# Gebruikt = staat in een var(...).
gebruikt="$(grep -oE 'var\(--theme-[a-z0-9-]+' "$zonder_commentaar" \
    | sed 's/^var(//' | sort -u)"

# 1. Elke gebruikte variabele moet gedefinieerd zijn.
for naam in $gebruikt; do
    if ! echo "$gedefinieerd" | grep -qxF -- "$naam"; then
        echo "::error file=$CSS::Variabele $naam wordt gebruikt met var() maar nergens gedefinieerd. Een ontbrekende CSS-variabele faalt stil: de eigenschap valt weg zonder foutmelding."
        fouten=$((fouten + 1))
    fi
done

# 2. Elke basiskleur moet compleet zijn: -light, -dark, en een effectieve verwijzing in beide
#    :root-blokken. Basis = een gedefinieerde naam zonder -light/-dark-achtervoegsel.
donker_blok="$(awk '/^:root\[data-theme="dark"\]/ { in_blok = 1 } in_blok { print } in_blok && /^}/ { in_blok = 0 }' "$zonder_commentaar")"

for naam in $gedefinieerd; do
    case "$naam" in
        *-light|*-dark) continue ;;
    esac

    if ! echo "$gedefinieerd" | grep -qxF -- "${naam}-light"; then
        echo "::error file=$CSS::$naam heeft geen ${naam}-light. Elke themakleur heeft een variant per modus nodig."
        fouten=$((fouten + 1))
    fi
    if ! echo "$gedefinieerd" | grep -qxF -- "${naam}-dark"; then
        echo "::error file=$CSS::$naam heeft geen ${naam}-dark. Elke themakleur heeft een variant per modus nodig."
        fouten=$((fouten + 1))
    fi
    if ! echo "$donker_blok" | grep -qE "^[[:space:]]*${naam}[[:space:]]*:"; then
        echo "::error file=$CSS::$naam wordt niet geherdefinieerd in het :root[data-theme=\"dark\"]-blok. Zonder die regel houdt de donkere modus de lichte waarde."
        fouten=$((fouten + 1))
    fi
done

aantal_basis=0
for naam in $gedefinieerd; do
    case "$naam" in
        *-light|*-dark) continue ;;
    esac
    aantal_basis=$((aantal_basis + 1))
done

if [ "$fouten" -gt 0 ]; then
    echo "Thema-variabele-guard: $fouten probleem(en) gevonden in $CSS."
    exit 1
fi

echo "Thema-variabele-guard: $aantal_basis themakleuren, elk met een licht- en donkervariant en een regel in het donkere blok. Geen ongedefinieerde var()-verwijzingen."
