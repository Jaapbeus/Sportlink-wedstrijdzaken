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
//
// #1702: wacht een PR na een schone review alleen nog op de merge, dan sluit de agent af met
// `Volgende beurt: merge door implementer`. Dat is geen label: `turn: claude-code` zou een nieuwe
// bot-run starten en `turn: owner` zou een wachtende merge als eigenaarsbesluit presenteren
// (zelfmerge-regel, AGENTS.md, #1690). Deze stap haalt dan alleen `turn: claude-code` weg, zet géén
// `turn:`-label en plaatst een issuecomment dat de eerstvolgende interactieve sessie de
// zelfmerge-voorwaarden controleert en merget (de bot merget nooit). Dat mag alleen in de
// verwerkingsfase (er is een start-PR) en alleen als die PR naar `develop` gaat en geen pad raakt
// dat volgens de zelfmerge-regel altijd naar de eigenaar gaat (werkinstructies/skills,
// databaseschema/migraties, Azure-infrastructuur); anders wordt het alsnog `turn: owner`.
//
// #1727: in de reviewfase (Claude Code reviewt een Codex-PR) roept de publicatiestap
// (claude-issue-turn-review.js) deze vergelijk-en-zet aan met een expliciet label (`turn: codex`, of
// `turn: owner` bij een blokkade) en de beoordeelde SHA: verandert de head van de start-PR, dan
// schrijft hij niets of draait hij zijn label terug. "Merge door implementer" wordt daar `turn: owner`.

const {
  findLinkedClosedPrs, isTrustedAuthor, labelNames,
  API_VERSION_HEADERS, TURN_LABEL_PREFIX, TURN_LABEL_CLAUDE, STATUS_AWAITING_RELEASE,
} = require('./claude-issue-turn-gate.js');

// De bot geeft de beurt nooit aan zichzelf (dat zou een nieuwe run starten).
const ALLOWED_END_LABELS = ['turn: codex', 'turn: owner'];
const FALLBACK_END_LABEL = 'turn: owner';
// #1727: fase van de poort; een reviewrun eindigt alleen op `turn: codex` of `turn: owner`.
const REVIEW_PHASE = 'review';
// #1702: geen label maar een uitkomst — de beurt blijft bij de implementer, die alleen nog merget.
const WAIT_FOR_MERGE = 'merge door implementer';
const MARKER = /^\s*Volgende beurt:\s*`?(turn: [a-z-]+|merge door implementer)`?\s*$/gm;
// Basisbranch waarop de implementer zelf mag mergen (zelfmerge-regel, AGENTS.md).
const SELF_MERGE_BASE = 'develop';
// Paden die volgens de zelfmerge-regel altijd naar de eigenaar gaan. Bewust ruim: een vals
// positief kost alleen een eigenaarsbeurt. `name` matcht een bestandsnaam in elke map.
const OWNER_ONLY_PATHS = [
  { name: 'AGENTS.md', reden: 'werkinstructies' },
  { name: 'CLAUDE.md', reden: 'werkinstructies' },
  { prefix: '.agents/', reden: 'skills' },
  { prefix: '.claude/', reden: 'skills/agentconfiguratie' },
  { prefix: 'Database/', reden: 'databaseschema' },
  { prefix: 'Database.Postgres/', reden: 'databaseschema/migraties' },
  { prefix: 'scripts/migrations/', reden: 'databaseschema/migraties' },
  { prefix: 'infrastructure/', reden: 'Azure-resources/kosten' },
  { prefix: 'scripts/azure/', reden: 'Azure-resources/kosten' },
];
// Boven deze grens geeft de API de bestandslijst niet volledig terug: dan niet zelf beslissen.
const MAX_LISTED_FILES = 3000;
const WAIT_COMMENT_HEADING = 'Wacht op merge door de implementer';

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
    if (ALLOWED_END_LABELS.includes(label) || label === WAIT_FOR_MERGE) return { label, source: 'beurtregel' };
    return { label: FALLBACK_END_LABEL, source: `ongeldige beurtregel '${label}'` };
  }
  return { label: FALLBACK_END_LABEL, source: 'geen beurtregel gevonden' };
}

/**
 * Leest de actuele toestand en geeft de reden terug waarom het eindlabel níet geschreven mag
 * worden, of null als de toestand sinds de start ongewijzigd is.
 */
async function changedSinceStart({
  github, context, issueNumber, startPrNumber, startedAt, expectedTurn = TURN_LABEL_CLAUDE, expectedSha = '',
}) {
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  const issue = (await github.rest.issues.get({ ...base, issue_number: issueNumber })).data;
  const labels = labelNames(issue);

  if (issue.state !== 'open') return `het issue is niet meer open (state: ${issue.state})`;
  if (labels.includes(STATUS_AWAITING_RELEASE)) return `het issue heeft \`${STATUS_AWAITING_RELEASE}\``;

  const turnLabels = labels.filter((n) => n.startsWith(TURN_LABEL_PREFIX));
  const nu = `(nu: ${turnLabels.length ? turnLabels.join(', ') : 'geen'})`;
  if (expectedTurn === null) {
    // #1702: na "wacht op merge" hoort er geen enkel turn-label meer te staan.
    if (turnLabels.length) return `er is intussen een turn-label gezet ${nu}`;
  } else if (turnLabels.length !== 1 || turnLabels[0] !== expectedTurn) {
    return `het turn-label is niet meer exclusief \`${expectedTurn}\` ${nu}`;
  }

  if (startPrNumber) {
    const pr = (await github.rest.pulls.get({ ...base, pull_number: startPrNumber })).data;
    if (pr.merged_at) return `PR #${startPrNumber} is gemerged`;
    if (pr.state !== 'open') return `PR #${startPrNumber} is gesloten`;
    // #1727: een review geldt alleen voor de beoordeelde SHA (AGENTS.md, "Review en turn:" regel 3).
    if (expectedSha && (pr.head?.sha || '').toLowerCase() !== expectedSha.toLowerCase()) {
      return `de head van PR #${startPrNumber} is niet meer de beoordeelde SHA`;
    }
  }

  const start = iso(startedAt);
  const closed = await findLinkedClosedPrs({ github, context, issueNumber, since: issue.created_at });
  const merged = closed.find((p) => p.mergedAt);
  if (merged) return `gekoppelde PR #${merged.number} is gemerged`;
  const closedDuringRun = closed.find((p) => p.closedAt && iso(p.closedAt) >= start);
  if (closedDuringRun) return `gekoppelde PR #${closedDuringRun.number} is tijdens de run gesloten`;

  return null;
}

/**
 * #1702: geeft de reden terug waarom "wacht op merge" niet mag (de beurt gaat dan naar de eigenaar),
 * of null. Deterministische tweede slot naast de prompt: base `develop` en geen eigenaarspad.
 */
function ownerOnlyReason(filename) {
  const base = filename.split('/').pop();
  const hit = OWNER_ONLY_PATHS.find((p) => (p.name ? base === p.name : filename.startsWith(p.prefix)));
  return hit ? `${hit.reden} (\`${filename}\`)` : null;
}

async function waitForMergeBlocker({ github, base, prNumber }) {
  if (!prNumber) return 'geen gekoppelde PR (alleen de verwerkingsfase kan op een merge wachten)';
  const pr = (await github.rest.pulls.get({ ...base, pull_number: prNumber })).data;
  if (pr.base?.ref !== SELF_MERGE_BASE) {
    return `PR #${prNumber} gaat naar \`${pr.base?.ref || '?'}\`, niet naar \`${SELF_MERGE_BASE}\``;
  }
  const files = await github.paginate(github.rest.pulls.listFiles, {
    ...base, pull_number: prNumber, per_page: 100,
  });
  if (files.length >= MAX_LISTED_FILES || (pr.changed_files && files.length < pr.changed_files)) {
    return `bestandslijst van PR #${prNumber} is onvolledig (${files.length}/${pr.changed_files})`;
  }
  for (const f of files) {
    for (const name of [f.filename, f.previous_filename].filter(Boolean)) {
      const reden = ownerOnlyReason(name);
      if (reden) return `harde uitzondering: ${reden}`;
    }
  }
  return null;
}

function waitForMergeComment(prNumber) {
  return [
    `**${WAIT_COMMENT_HEADING}** (PR #${prNumber})`,
    '',
    'De CI-run sloot af zonder openstaand eigenaarspunt en zonder harde uitzondering. Daarom staat',
    'er bewust geen `turn:`-label: `turn: claude-code` zou een nieuwe bot-run starten en dit is geen',
    'eigenaarsbesluit. De eerstvolgende interactieve sessie controleert de zelfmerge-voorwaarden',
    '(AGENTS.md, "Zelfmerge na schone review"), zet de PR op ready en merget naar `develop`;',
    'ontbreekt een voorwaarde, dan fase `eigenaarsbesluit` met `turn: owner`. De bot merget nooit (#1702).',
  ].join('\n');
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
async function applyEndLabel({
  github, context, core, issueNumber, startPrNumber, startedAt, phase = '', requestedLabel = '', expectedSha = '',
}) {
  if (!startedAt || Number.isNaN(new Date(startedAt).getTime())) {
    // Zonder starttijd valt "veranderd tijdens de run" niet te bepalen: niets schrijven.
    return { written: false, label: '', source: '', reason: 'starttijd van de run ontbreekt' };
  }
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  let label;
  let source;
  if (requestedLabel) {
    // #1727: de reviewpublicatie bepaalt het label zelf uit de gevalideerde uitvoer; geen agentcomment.
    label = ALLOWED_END_LABELS.includes(requestedLabel) ? requestedLabel : FALLBACK_END_LABEL;
    source = ALLOWED_END_LABELS.includes(requestedLabel) ? 'reviewpublicatie' : `ongeldig label '${requestedLabel}'`;
  } else {
    const comments = await github.paginate(github.rest.issues.listComments, {
      ...base, issue_number: issueNumber, since: iso(startedAt), per_page: 100,
    });
    ({ label, source } = requestedEndLabel(comments, startedAt));
  }

  if (phase === REVIEW_PHASE && label === WAIT_FOR_MERGE) {
    // #1727: de reviewer geeft altijd terug aan de implementer (AGENTS.md, "Review en turn:" regel 3),
    // ook zonder bevindingen; of er gemerged wordt, beoordeelt de implementer.
    label = FALLBACK_END_LABEL;
    source = 'merge door implementer is geen uitkomst van een reviewrun';
  }

  if (label === WAIT_FOR_MERGE) {
    const blocker = await waitForMergeBlocker({ github, base, prNumber: startPrNumber });
    if (blocker) {
      label = FALLBACK_END_LABEL;
      source = `merge door implementer geweigerd: ${blocker}`;
    }
  }

  const before = await changedSinceStart({ github, context, issueNumber, startPrNumber, startedAt, expectedSha });
  if (before) {
    core.notice(`#${issueNumber}: eindlabel '${label}' niet geschreven: ${before}`);
    return { written: false, label, source, reason: before };
  }

  if (label === WAIT_FOR_MERGE) {
    // Alleen het startlabel weghalen; geen eigen label, dus bij een latere verandering valt er
    // niets terug te draaien. Het comment volgt pas als de nacontrole niets veranderd ziet.
    await removeIfPresent({ github, base, issueNumber, name: TURN_LABEL_CLAUDE });
    const after = await changedSinceStart({
      github, context, issueNumber, startPrNumber, startedAt, expectedTurn: null,
    });
    if (after) {
      core.warning(`#${issueNumber}: 'wacht op merge' niet vastgelegd: ${after}`);
      return { written: false, label, source, reason: after };
    }
    await github.rest.issues.createComment({
      ...base, issue_number: issueNumber, body: waitForMergeComment(startPrNumber),
    });
    core.notice(`#${issueNumber}: geen turn-label; wacht op merge door de implementer (PR #${startPrNumber})`);
    return { written: true, label, source };
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
    github, context, issueNumber, startPrNumber, startedAt, expectedTurn: label, expectedSha,
  });
  if (after) {
    // Een mislukte verwijdering propageert (job faalt zichtbaar); alleen "al weg" (404) is succes.
    await removeIfPresent({ github, base, issueNumber, name: label });
    core.warning(`#${issueNumber}: eindlabel '${label}' teruggedraaid: ${after}`);
    return { written: false, label, source, reason: after, rolledBack: true };
  }
  return { written: true, label, source };
}

module.exports = {
  applyEndLabel, requestedEndLabel, changedSinceStart, ownerOnlyReason,
  ALLOWED_END_LABELS, WAIT_FOR_MERGE, WAIT_COMMENT_HEADING,
};
