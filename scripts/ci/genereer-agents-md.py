#!/usr/bin/env python3
"""genereer-agents-md.py (#1262) — leidt AGENTS.md af uit CLAUDE.md.

WAAROM DIT SCRIPT BESTAAT
-------------------------
CLAUDE.md en AGENTS.md moeten hetzelfde zeggen: het zijn dezelfde projectregels, voor twee
verschillende agents. Ze werden met de hand synchroon gehouden. Dat ging mis, precies zoals
#1248 met de thema-logica misging — en met dezelfde onzichtbaarheid, want niets vergeleek ze.

Stand op 2026-09-19, gemeten vlak voor de invoering van dit script: AGENTS.md was 280 regels
korter en miste negen volledige secties die CLAUDE.md wel had, waaronder:

  * "Multi-tier databasestrategie — vaste bouwvolgorde, geen gedeelde abstractie"
  * "Teamnaam -> TeamId: een vertaalpunt, nooit een nieuwe regex elders"
  * "Uitgaande integraties — altijd via EgressGuard (#857)"

De eerste twee zijn precies de regels die duplicatie moeten tegenhouden; de derde is een
beveiligingsregel. De tweede reviewer van dit project werkte er dus zonder.

Het antwoord op twee documenten die hetzelfde moeten zeggen, is niet ze harder synchroniseren
maar ze niet langer allebei met de hand schrijven. CLAUDE.md is de bron; AGENTS.md wordt
afgeleid, net zoals openapi.json uit openapi.yaml wordt afgeleid.

GEBRUIK
-------
    python3 scripts/ci/genereer-agents-md.py            # controleer (exit 1 bij verschil)
    python3 scripts/ci/genereer-agents-md.py --schrijf  # regenereer AGENTS.md

DE AFLEIDING
------------
De inhoud is gedeeld voor beide agents. Alleen de documenttitel wordt aangepast;
agentnamen, rollen, bevoegdheden, paden en URL's blijven letterlijk behouden.
"""

import sys
from pathlib import Path

WORTEL = Path(__file__).resolve().parents[2]
BRON = WORTEL / "CLAUDE.md"
DOEL = WORTEL / "AGENTS.md"

KOP = """<!-- GEGENEREERD BESTAND — NIET MET DE HAND BEWERKEN.

     Afgeleid uit CLAUDE.md door scripts/ci/genereer-agents-md.py (#1262).
     Wijzig CLAUDE.md en draai daarna:

         python3 scripts/ci/genereer-agents-md.py --schrijf

     De CI-job 'Build FunctionApp + BlazorAdmin' faalt als dit bestand niet overeenkomt met
     CLAUDE.md. Reden: AGENTS.md liep 280 regels en negen hele secties achter toen beide
     bestanden nog met de hand werden bijgehouden — zie de scriptkop.
-->

"""


def genereer(bron_tekst: str) -> str:
    return KOP + bron_tekst.replace("# CLAUDE.md", "# AGENTS.md", 1)


def main() -> int:
    if not BRON.is_file():
        print(f"::error::{BRON} ontbreekt — dit script kan niets afleiden.", file=sys.stderr)
        return 1

    verwacht = genereer(BRON.read_text(encoding="utf-8"))

    if "--schrijf" in sys.argv:
        DOEL.write_text(verwacht, encoding="utf-8")
        print(f"AGENTS.md geschreven ({len(verwacht.splitlines())} regels) uit CLAUDE.md.")
        return 0

    huidig = DOEL.read_text(encoding="utf-8") if DOEL.is_file() else ""
    if huidig == verwacht:
        print(f"OK — AGENTS.md komt overeen met CLAUDE.md ({len(verwacht.splitlines())} regels).")
        return 0

    print("::error file=AGENTS.md::AGENTS.md wijkt af van CLAUDE.md. Regenereer met: "
          "python3 scripts/ci/genereer-agents-md.py --schrijf")
    print("::error::Bewerk AGENTS.md nooit met de hand — de wijziging hoort in CLAUDE.md. "
          "Toen beide bestanden nog met de hand werden bijgehouden, miste AGENTS.md negen "
          "secties, waaronder de tier-regel, de teamnormalisatieregel en de EgressGuard-regel.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
