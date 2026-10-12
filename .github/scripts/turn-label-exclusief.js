// Houdt het `turn:`-label van een issue exclusief (#1686): bij meerdere `turn:`-labels blijft het label
// met het meest recente `labeled`-event staan en worden de andere verwijderd (laatste schrijver wint).
//
// Aanleiding: twee schrijvers (de issue-beurt-bot en een handmatige sessie) wisselden de beurt voor
// hetzelfde issue; een gerichte vervanging van één label liet het label van de ander staan. AGENTS.md
// vereist hoogstens één `turn:`-label, maar niets dwong dat af. De helper draait alleen op zijn eigen
// trigger (een gelabeld issue), dus de tests hierbij (turn-label-exclusief.test.js) draaien in build.yml.

const TURN_PREFIX = 'turn: ';
const API_VERSION_HEADERS = { 'X-GitHub-Api-Version': '2022-11-28' };

function labelNames(issue) {
  return (issue.labels || []).map((l) => (typeof l === 'string' ? l : l.name));
}

/**
 * Kiest welk `turn:`-label blijft. Zuiver en deterministisch.
 * @param {string[]} turnLabels   alle `turn:`-labels die nu op het issue staan
 * @param {object[]} events       issue-events (labeled/unlabeled) met created_at en label.name
 * @param {string|null} ontvangen het label waarop deze run startte (terugval)
 * @returns {string|null}
 */
function kiesTeBehoudenLabel({ turnLabels, events = [], ontvangen = null }) {
  if (turnLabels.length === 0) return null;
  if (turnLabels.length === 1) return turnLabels[0];

  // Een label zonder `labeled`-event in de opgehaalde events is later gezet dan die events (de
  // momentopnamen liggen op verschillende tijdstippen): het is dus het nieuwste. Meerdere zulke
  // labels: het laatst genoemde wint; de hercontrole in maakTurnExclusief rondt het af.
  const metEvent = new Set(events.filter((e) => e.event === 'labeled' && e.label).map((e) => e.label.name));
  const zonderEvent = turnLabels.filter((n) => !metEvent.has(n));
  if (zonderEvent.length > 0) return zonderEvent[zonderEvent.length - 1];

  let beste = null;
  let besteTijd = '';
  for (const e of events) {
    if (e.event !== 'labeled' || !e.label || !turnLabels.includes(e.label.name)) continue;
    // >= : bij gelijke tijdstempel wint het later genoemde event (de API levert oplopend).
    if ((e.created_at || '') >= besteTijd) {
      beste = e.label.name;
      besteTijd = e.created_at || '';
    }
  }
  if (beste) return beste;
  if (ontvangen && turnLabels.includes(ontvangen)) return ontvangen;
  return turnLabels[turnLabels.length - 1];
}

/**
 * Verwijdert alle `turn:`-labels behalve het te behouden label en VERIFIEERT daarna de eindtoestand.
 * Volgorde per poging: labels lezen, events lezen, labels opnieuw lezen (de tweede lezing is leidend:
 * een label dat tussendoor is bijgekomen telt mee), kiezen, verwijderen, en opnieuw lezen. Staan er dan
 * nog meerdere `turn:`-labels (een andere schrijver was ertussen), dan volgt een nieuwe poging (begrensd).
 * Succes wordt alleen gemeld als de laatste lezing hoogstens één `turn:`-label laat zien. Een
 * verwijdering die anders dan "al afwezig" (404) mislukt, laat de run hard falen.
 * @returns {Promise<{behouden: string|null, verwijderd: string[]}>}
 */
async function maakTurnExclusief({ github, context, core, issueNumber, ontvangenLabel = null, maxPogingen = 3 }) {
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  const leesTurnLabels = async () => {
    const issue = (await github.rest.issues.get({ ...base, issue_number: issueNumber })).data;
    return { issue, turnLabels: labelNames(issue).filter((n) => n.startsWith(TURN_PREFIX)) };
  };

  const verwijderd = [];
  for (let poging = 1; poging <= maxPogingen; poging += 1) {
    const eerste = await leesTurnLabels();
    if (eerste.issue.pull_request) return { behouden: null, verwijderd };
    if (eerste.turnLabels.length <= 1 && poging === 1) {
      // Snelle uitweg; de hercontrole hieronder geldt alleen na een verwijdering.
      return { behouden: eerste.turnLabels[0] || null, verwijderd };
    }

    const events = await github.paginate(github.rest.issues.listEvents, {
      ...base, issue_number: issueNumber, per_page: 100,
    });
    const { turnLabels } = await leesTurnLabels(); // leidende momentopname (na de events)
    const behouden = kiesTeBehoudenLabel({ turnLabels, events, ontvangen: ontvangenLabel });

    const mislukt = [];
    for (const stale of turnLabels.filter((n) => n !== behouden)) {
      try {
        await github.rest.issues.removeLabel({ ...base, issue_number: issueNumber, name: stale });
        verwijderd.push(stale);
      } catch (e) {
        if (e && e.status === 404) {
          verwijderd.push(stale); // aantoonbaar al afwezig: idempotent
        } else {
          mislukt.push(stale);
          core.warning(`#${issueNumber}: '${stale}' verwijderen mislukt (${e && e.message})`);
        }
      }
    }
    if (mislukt.length > 0) {
      throw new Error(`#${issueNumber}: turn-label(s) niet verwijderd: ${mislukt.join(', ')}`);
    }

    const na = await leesTurnLabels();
    if (na.turnLabels.length <= 1) {
      if (verwijderd.length > 0) {
        core.notice(`#${issueNumber}: turn-label '${na.turnLabels[0] || behouden}' behouden, verwijderd: ${verwijderd.join(', ')}`);
      }
      return { behouden: na.turnLabels[0] || null, verwijderd };
    }
    core.warning(`#${issueNumber}: na poging ${poging} nog ${na.turnLabels.length} turn-labels (${na.turnLabels.join(', ')}); hercontrole`);
  }
  throw new Error(`#${issueNumber}: turn-labels niet exclusief na ${maxPogingen} pogingen`);
}

module.exports = { TURN_PREFIX, kiesTeBehoudenLabel, maakTurnExclusief };
