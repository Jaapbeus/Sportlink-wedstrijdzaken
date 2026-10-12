// claude-issue-turn-eindlabel.js
//
// Eindlabel van claude-issue-turn.yml als vergelijk-en-zet (#1692). De agent in de CI-run schrijft
// zelf geen `turn:`-label meer (`gh issue edit` staat niet meer in zijn --allowedTools); hij sluit
// zijn laatste issuecomment af met één regel `Volgende beurt: turn: codex` of `Volgende beurt:
// turn: owner`. Deze stap draait daarna in een eigen job op code van de default branch (net als de
// poort, #1612) en schrijft dat label alleen als de toestand sinds het begin van de run niet is
// veranderd:
//
//   - het issue is nog open en heeft geen `status: awaiting-release`;
//   - het enige `turn:`-label is nog `turn: claude-code` (het label waarop de run startte);
//   - de PR waarmee de run begon is nog open, en geen gekoppelde PR is gemerged of tijdens de run
//     gesloten.
//
// Anders schrijft hij niets en meldt hij de reden (run-samenvatting via de workflow). Aanleiding:
// op #1675 schreef de bot `turn: owner` zes seconden nadat een interactieve sessie de PR had
// gemerged en het issue had opgeruimd.
//
// GitHub kent geen voorwaardelijke labelschrijfactie; tussen lezen en schrijven blijft een venster
// van één API-aanroep. Daarom wordt na het schrijven opnieuw gelezen: is de toestand dan alsnog
// veranderd, dan haalt deze stap uitsluitend het eigen, zojuist gezette label weer weg.

const {
  findLinkedClosedPrs, isTrustedAuthor, labelNames,
  API_VERSION_HEADERS, TURN_LABEL_PREFIX, TURN_LABEL_CLAUDE, STATUS_AWAITING_RELEASE,
} = require('./claude-issue-turn-gate.js');

// De bot geeft de beurt nooit aan zichzelf (dat zou een nieuwe run starten).
const ALLOWED_END_LABELS = ['turn: codex', 'turn: owner'];
const FALLBACK_END_LABEL = 'turn: owner';
const MARKER = /^\s*Volgende beurt:\s*`?(turn: [a-z-]+)`?\s*$/gm;

function iso(value) {
  return value ? new Date(value).toISOString() : '';
}

/**
 * Het gevraagde eindlabel: de laatste markeerregel in het nieuwste issuecomment van de Claude
 * GitHub App dat tijdens deze run is geplaatst. Comments van anderen tellen niet; een ontbrekende
 * of ongeldige regel geeft de beurt aan de eigenaar.
 */
function requestedEndLabel(comments, startedAt) {
  const start = iso(startedAt);
  const own = comments
    .filter((c) => c.user?.type === 'Bot' && isTrustedAuthor(c))
    .filter((c) => iso(c.created_at) >= start)
    .sort((a, b) => (iso(a.created_at) < iso(b.created_at) ? -1 : 1));
  for (let i = own.length - 1; i >= 0; i -= 1) {
    const found = [...(own[i].body || '').matchAll(MARKER)].map((m) => m[1]);
    if (!found.length) continue;
    const label = found[found.length - 1];
    if (ALLOWED_END_LABELS.includes(label)) return { label, source: 'beurtregel' };
    return { label: FALLBACK_END_LABEL, source: `ongeldige beurtregel '${label}'` };
  }
  return { label: FALLBACK_END_LABEL, source: 'geen beurtregel gevonden' };
}

/**
 * Leest de actuele toestand en geeft de reden terug waarom het eindlabel níet geschreven mag
 * worden, of null als de toestand sinds de start ongewijzigd is.
 */
async function changedSinceStart({
  github, context, issueNumber, startPrNumber, startedAt, expectedTurn = TURN_LABEL_CLAUDE,
}) {
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  const issue = (await github.rest.issues.get({ ...base, issue_number: issueNumber })).data;
  const labels = labelNames(issue);

  if (issue.state !== 'open') return `het issue is niet meer open (state: ${issue.state})`;
  if (labels.includes(STATUS_AWAITING_RELEASE)) return `het issue heeft \`${STATUS_AWAITING_RELEASE}\``;

  const turnLabels = labels.filter((n) => n.startsWith(TURN_LABEL_PREFIX));
  if (turnLabels.length !== 1 || turnLabels[0] !== expectedTurn) {
    return `het turn-label is niet meer exclusief \`${expectedTurn}\` ` +
      `(nu: ${turnLabels.length ? turnLabels.join(', ') : 'geen'})`;
  }

  if (startPrNumber) {
    const pr = (await github.rest.pulls.get({ ...base, pull_number: startPrNumber })).data;
    if (pr.merged_at) return `PR #${startPrNumber} is gemerged`;
    if (pr.state !== 'open') return `PR #${startPrNumber} is gesloten`;
  }

  const start = iso(startedAt);
  const closed = await findLinkedClosedPrs({ github, context, issueNumber, since: issue.created_at });
  const merged = closed.find((p) => p.mergedAt);
  if (merged) return `gekoppelde PR #${merged.number} is gemerged`;
  const closedDuringRun = closed.find((p) => p.closedAt && iso(p.closedAt) >= start);
  if (closedDuringRun) return `gekoppelde PR #${closedDuringRun.number} is tijdens de run gesloten`;

  return null;
}

async function removeIfPresent({ github, base, issueNumber, name }) {
  try {
    await github.rest.issues.removeLabel({ ...base, issue_number: issueNumber, name });
  } catch (err) {
    if (err?.status !== 404) throw err;
  }
}

/**
 * @returns {Promise<{written: boolean, label: string, source: string, reason?: string,
 *   rolledBack?: boolean}>}
 */
async function applyEndLabel({ github, context, core, issueNumber, startPrNumber, startedAt }) {
  if (!startedAt || Number.isNaN(new Date(startedAt).getTime())) {
    // Zonder starttijd valt "veranderd tijdens de run" niet te bepalen: niets schrijven.
    return { written: false, label: '', source: '', reason: 'starttijd van de run ontbreekt' };
  }
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  const comments = await github.paginate(github.rest.issues.listComments, {
    ...base, issue_number: issueNumber, since: iso(startedAt), per_page: 100,
  });
  const { label, source } = requestedEndLabel(comments, startedAt);

  const before = await changedSinceStart({ github, context, issueNumber, startPrNumber, startedAt });
  if (before) {
    core.notice(`#${issueNumber}: eindlabel '${label}' niet geschreven: ${before}`);
    return { written: false, label, source, reason: before };
  }

  // Bewust geen setTurnLabel(): die leest het issue opnieuw en zou een beurtwijziging die na de
  // voorcontrole is gezet alsnog overschrijven. De voorcontrole bewees dat `turn: claude-code` het
  // enige turn-label was; alleen dat label vervangen we, zonder tweede lezing.
  await github.rest.issues.addLabels({
    ...base, issue_number: issueNumber, labels: [label],
  });
  await removeIfPresent({ github, base, issueNumber, name: TURN_LABEL_CLAUDE });
  core.notice(`#${issueNumber}: turn-label → '${label}'`);

  // Venster tussen lezen en schrijven: opnieuw controleren. Nu moet het eigen label het enige
  // turn-label zijn; een door een ander gezet label (of merge/afsluiting) betekent terugdraaien.
  const after = await changedSinceStart({
    github, context, issueNumber, startPrNumber, startedAt, expectedTurn: label,
  });
  if (after) {
    // Een mislukte verwijdering propageert (job faalt zichtbaar); alleen "al weg" (404) is succes.
    await removeIfPresent({ github, base, issueNumber, name: label });
    core.warning(`#${issueNumber}: eindlabel '${label}' teruggedraaid: ${after}`);
    return { written: false, label, source, reason: after, rolledBack: true };
  }
  return { written: true, label, source };
}

module.exports = { applyEndLabel, requestedEndLabel, changedSinceStart, ALLOWED_END_LABELS };
