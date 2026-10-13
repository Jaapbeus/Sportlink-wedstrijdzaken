// Houdt het `turn:`-label van een issue exclusief (#1686): bij meerdere `turn:`-labels blijft het label
// met de meest recente toevoeging staan en worden de andere verwijderd (laatste schrijver wint).
//
// Aanleiding: twee schrijvers (de issue-beurt-bot en een handmatige sessie) wisselden de beurt voor
// hetzelfde issue; een gerichte vervanging van één label liet het label van de ander staan. AGENTS.md
// vereist hoogstens één `turn:`-label, maar niets dwong dat af. De helper draait alleen op zijn eigen
// trigger (een gelabeld issue), dus de tests hierbij (turn-label-exclusief.test.js) draaien in build.yml.
//
// Bewijsregel (Codex r2 #1687): een keuze steunt alleen op een CONSISTENTE momentopname. Eerst de labels,
// daarna de events; consistent betekent dat het afspelen van de `turn:`-events precies de gelezen labels
// oplevert (elk label op het issue heeft als laatste event een toevoeging, en geen label is volgens de
// events aanwezig dat er niet staat). Elke wijziging tussen beide lezingen verschijnt dan in de events en
// valt op, of is zelf de nieuwste toevoeging. Bij twijfel (achterlopende events, een label zonder event,
// een historisch event voor een opnieuw gezet label) wordt niets verwijderd en opnieuw gelezen.
//
// Restgrens: GitHub kent geen voorwaardelijke labelverwijdering. Wordt een label dat deze run verwijdert in
// het korte venster tussen de momentopname en de verwijdering weggehaald én opnieuw gezet, dan is uit de
// events niet vast te stellen of onze verwijdering vóór of ná die nieuwe toevoeging viel. De hercontrole
// ziet dat (een nieuwe toevoeging van een door ons verwijderd, nu afwezig label) en laat de run dan hard
// falen in plaats van succes te melden; herstel is dan handwerk.

const TURN_PREFIX = 'turn: ';
const API_VERSION_HEADERS = { 'X-GitHub-Api-Version': '2022-11-28' };

function labelNames(issue) {
  return (issue.labels || []).map((l) => (typeof l === 'string' ? l : l.name));
}

function isTurnEvent(e) {
  return Boolean(e && (e.event === 'labeled' || e.event === 'unlabeled') && e.label
    && typeof e.label.name === 'string' && e.label.name.startsWith(TURN_PREFIX));
}

function eventSleutel(e) {
  if (e && e.id != null) return `id:${e.id}`;
  return `${e && e.event}|${e && e.label && e.label.name}|${e && e.created_at}`;
}

/** Per `turn:`-label het laatste event (in API-volgorde, oplopend): toestand, tijd en positie. */
function speelAf(events) {
  const laatste = new Map();
  events.forEach((e, index) => {
    if (isTurnEvent(e)) laatste.set(e.label.name, { event: e.event, tijd: e.created_at || '', index });
  });
  return laatste;
}

/**
 * Of de events precies de gelezen `turn:`-labels verklaren (zie de bewijsregel bovenaan).
 * @param {string[]} turnLabels
 * @param {object[]} events
 */
function isConsistent(turnLabels, events) {
  const laatste = speelAf(events);
  const aanwezig = new Set(turnLabels);
  for (const naam of turnLabels) {
    const l = laatste.get(naam);
    if (!l || l.event !== 'labeled') return false;
  }
  for (const [naam, l] of laatste) {
    if (l.event === 'labeled' && !aanwezig.has(naam)) return false;
  }
  return true;
}

/**
 * Kiest welk `turn:`-label blijft. Zuiver en deterministisch.
 * @param {string[]} turnLabels  alle `turn:`-labels die nu op het issue staan
 * @param {object[]} events      issue-events (labeled/unlabeled), gelezen NA de labels
 * @returns {string|null}  het label met de meest recente toevoeging; null bij twijfel (momentopname
 *                          niet consistent) of zonder labels. Bij null mag niets worden verwijderd.
 */
function kiesTeBehoudenLabel({ turnLabels, events = [] }) {
  if (turnLabels.length === 0) return null;
  if (turnLabels.length === 1) return turnLabels[0];
  if (!isConsistent(turnLabels, events)) return null;

  const laatste = speelAf(events);
  let beste = null;
  for (const naam of turnLabels) {
    const l = laatste.get(naam);
    // Gelijke tijdstempel: het later genoemde event wint (de API levert oplopend).
    if (!beste || l.tijd > beste.tijd || (l.tijd === beste.tijd && l.index > beste.index)) beste = { naam, ...l };
  }
  return beste.naam;
}

/**
 * Verwijdert alle `turn:`-labels behalve het nieuwste en VERIFIEERT daarna de eindtoestand.
 * Per poging: labels lezen, events lezen (momentopname). Niet consistent: niets verwijderen, opnieuw.
 * Consistent: eerst de hercontrole van de vorige verwijderronde (zie restgrens), dan klaar bij hoogstens
 * één label, anders het nieuwste houden en de rest verwijderen. Succes wordt alleen gemeld vanuit een
 * consistente momentopname met hoogstens één `turn:`-label. Een verwijdering die anders dan "al afwezig"
 * (404) mislukt, laat de run hard falen; na `maxPogingen` zonder exclusiviteit ook.
 * @returns {Promise<{behouden: string|null, verwijderd: string[]}>}
 */
async function maakTurnExclusief({ github, context, core, issueNumber, maxPogingen = 5 }) {
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  const leesTurnLabels = async () => {
    const issue = (await github.rest.issues.get({ ...base, issue_number: issueNumber })).data;
    return { issue, turnLabels: labelNames(issue).filter((n) => n.startsWith(TURN_PREFIX)) };
  };
  const leesEvents = () => github.paginate(github.rest.issues.listEvents, {
    ...base, issue_number: issueNumber, per_page: 100,
  });

  const eerste = await leesTurnLabels();
  if (eerste.issue.pull_request) return { behouden: null, verwijderd: [] };
  // Snelle uitweg: niets te verwijderen, dus ook niets te bewijzen.
  if (eerste.turnLabels.length <= 1) return { behouden: eerste.turnLabels[0] || null, verwijderd: [] };

  const verwijderd = [];
  let grens = null; // sleutels van de events uit de momentopname waarop de laatste verwijderronde steunde
  let onsVerwijderd = []; // labels die die ronde echt verwijderde (niet: al afwezig)
  for (let poging = 1; poging <= maxPogingen; poging += 1) {
    const { turnLabels } = poging === 1 ? eerste : await leesTurnLabels();
    const events = await leesEvents();
    if (!isConsistent(turnLabels, events)) {
      core.warning(`#${issueNumber}: labels en events verschillen na poging ${poging} (${turnLabels.join(', ')}); niets verwijderd, opnieuw lezen`);
      continue;
    }

    if (grens) {
      const twijfel = onsVerwijderd.filter((naam) => !turnLabels.includes(naam) && events.some((e) =>
        isTurnEvent(e) && e.event === 'labeled' && e.label.name === naam && !grens.has(eventSleutel(e))));
      if (twijfel.length > 0) {
        throw new Error(`#${issueNumber}: ${twijfel.join(', ')} werd tijdens het opruimen opnieuw gezet en is mogelijk door deze run verwijderd; controleer de turn-labels handmatig`);
      }
      grens = null;
      onsVerwijderd = [];
    }

    if (turnLabels.length <= 1) {
      if (verwijderd.length > 0) {
        core.notice(`#${issueNumber}: turn-label '${turnLabels[0] || '(geen)'}' behouden, verwijderd: ${verwijderd.join(', ')}`);
      }
      return { behouden: turnLabels[0] || null, verwijderd };
    }

    const behouden = kiesTeBehoudenLabel({ turnLabels, events });
    const dezeRonde = [];
    const mislukt = [];
    for (const stale of turnLabels.filter((n) => n !== behouden)) {
      try {
        await github.rest.issues.removeLabel({ ...base, issue_number: issueNumber, name: stale });
        dezeRonde.push(stale);
        verwijderd.push(stale);
      } catch (e) {
        if (e && e.status === 404) {
          verwijderd.push(stale); // aantoonbaar al afwezig: idempotent, niet door ons verwijderd
        } else {
          mislukt.push(stale);
          core.warning(`#${issueNumber}: '${stale}' verwijderen mislukt (${e && e.message})`);
        }
      }
    }
    if (mislukt.length > 0) {
      throw new Error(`#${issueNumber}: turn-label(s) niet verwijderd: ${mislukt.join(', ')}`);
    }
    grens = new Set(events.map(eventSleutel));
    onsVerwijderd = dezeRonde;
  }
  throw new Error(`#${issueNumber}: turn-labels niet exclusief na ${maxPogingen} pogingen`);
}

module.exports = { TURN_PREFIX, isConsistent, kiesTeBehoudenLabel, maakTurnExclusief };
