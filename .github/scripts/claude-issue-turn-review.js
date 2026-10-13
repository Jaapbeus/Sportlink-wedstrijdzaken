// claude-issue-turn-review.js
//
// Publicatie van een review door Claude Code op een Codex-PR (#1727). De review-agent in
// claude-issue-turn.yml heeft geen schrijfrechten en publiceert zelf niets: hij levert alleen
// gestructureerde uitvoer (REVIEW_SCHEMA). Deze stap draait daarna in een eigen job op code van de
// default branch (zoals de poort, #1612), met de GITHUB_TOKEN van de workflow, en:
//
//   1. valideert de uitvoer (vorm, waarden, lengtes) en scant de te publiceren tekst op waarden die
//      niet publiek mogen (AGENTS.md §4a); bij twijfel wordt niets gepubliceerd;
//   2. controleert vlak vóór de eerste comment opnieuw de aanvraag: issue open, exclusief
//      `turn: claude-code`, de reviewaanvraag is nog de nieuwste fasecomment voor dezelfde PR en
//      SHA, de PR is nog de enige gekoppelde open Codex-PR met die SHA als head, en de SHA is nog
//      niet beoordeeld (reviewprotocol, regel 3);
//   3. plaatst de reviewteruggave op de PR (met een markering per SHA, zodat de poort hem als
//      "al beoordeeld" ziet) en een korte teruggave op het issue;
//   4. zet het beurtlabel via de vergelijk-en-zet van claude-issue-turn-eindlabel.js, gebonden aan
//      dezelfde SHA, na nog één controle van de aanvraag.
//
// Faalt stap 1 of 2, dan publiceert deze stap niets, wijzigt hij geen label en faalt de job zichtbaar.

const {
  findReviewRequest, findLinkedOpenPrsByKind, isShaAlreadyReviewed, reviewMarker, labelNames,
  API_VERSION_HEADERS, TURN_LABEL_PREFIX, TURN_LABEL_CLAUDE, STATUS_AWAITING_RELEASE,
} = require('./claude-issue-turn-gate.js');
const { applyEndLabel } = require('./claude-issue-turn-eindlabel.js');

const PRIORITEITEN = ['P1', 'P2', 'P3'];
const UITKOMSTEN = ['geen bevindingen', 'bevindingen'];
const MAX_BEVINDINGEN = 20;
const MAX = { locatie: 200, reden: 2000, verificatie: 1000, samenvatting: 2000 };

// Moet overeenkomen met de `--json-schema` in claude-issue-turn.yml (de test controleert dat).
const REVIEW_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['uitvoerbaar', 'uitkomst', 'bevindingen', 'besloten_bevinding', 'samenvatting'],
  properties: {
    uitvoerbaar: { type: 'boolean' },
    uitkomst: { type: 'string', enum: UITKOMSTEN },
    bevindingen: {
      type: 'array',
      maxItems: MAX_BEVINDINGEN,
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['prioriteit', 'locatie', 'reden', 'verificatie'],
        properties: {
          prioriteit: { type: 'string', enum: PRIORITEITEN },
          locatie: { type: 'string', maxLength: MAX.locatie },
          reden: { type: 'string', maxLength: MAX.reden },
          verificatie: { type: 'string', maxLength: MAX.verificatie },
        },
      },
    },
    besloten_bevinding: { type: 'boolean' },
    samenvatting: { type: 'string', maxLength: MAX.samenvatting },
  },
};

// Grove vangnetten voor waarden die nooit publiek mogen (AGENTS.md §4a). Een treffer = niets publiceren.
// Geen IPv4-patroon: de versienummers van deze repo (MAJOR.MINOR.PATCH.REVISION) zien er net zo uit.
const VERBODEN = [
  { naam: 'GUID', re: /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i },
  { naam: 'SWA-hostnaam', re: /[a-z0-9-]+\.(?:\d+\.)?azurestaticapps\.net/i },
  { naam: 'GitHub-token', re: /\bgh[pousr]_[A-Za-z0-9]{20,}/ },
  { naam: 'JWT', re: /\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\./ },
];
const EMAIL_RE = /[A-Z0-9._%+-]+@([A-Z0-9.-]+\.[A-Z]{2,})/gi;
// Toegestane testdomeinen (ARCHITECTUUR.md §8.1.5) en gereserveerde voorbeelddomeinen.
const TOEGESTANE_DOMEINEN = /^(?:voorbeeld\.nl|allstars-fc\.test|[a-z0-9.-]+\.test|example\.(?:com|org|net))$/i;

/**
 * Ingevoegde tekst mag geen HTML openen: geen markering nabootsen, geen commentaar of tag die de
 * opmaak breekt. Elke `<` en `>` wordt een gelijkend, onschuldig teken; verwijderen van alleen
 * `<!--` is te omzeilen (`<!<!---- ` wordt dan weer `<!--`, CodeQL js/incomplete-multi-character-sanitization).
 */
function schoon(text, max, { eenRegel = false } = {}) {
  let t = String(text ?? '').replace(/</g, '‹').replace(/>/g, '›').replace(/\r/g, '');
  if (eenRegel) t = t.replace(/\s*\n\s*/g, ' ');
  t = t.trim();
  return t.length > max ? `${t.slice(0, max - 1)}…` : t;
}

/**
 * Leest en valideert de gestructureerde uitvoer van de agent. Geeft { ok, data } of { ok: false, reason }.
 * `raw` is de JSON-tekst uit de action-output `structured_output` (of al een object).
 */
function parseReviewOutput(raw) {
  let data = raw;
  if (typeof raw === 'string') {
    if (!raw.trim()) return { ok: false, reason: 'de agent leverde geen gestructureerde uitvoer' };
    try { data = JSON.parse(raw); } catch { return { ok: false, reason: 'de uitvoer is geen geldige JSON' }; }
  }
  if (!data || typeof data !== 'object' || Array.isArray(data)) return { ok: false, reason: 'de uitvoer is geen object' };
  const extra = Object.keys(data).filter((k) => !(k in REVIEW_SCHEMA.properties));
  if (extra.length) return { ok: false, reason: `onbekende velden: ${extra.join(', ')}` };
  if (typeof data.uitvoerbaar !== 'boolean' || typeof data.besloten_bevinding !== 'boolean') {
    return { ok: false, reason: 'uitvoerbaar/besloten_bevinding ontbreekt of is geen boolean' };
  }
  if (!UITKOMSTEN.includes(data.uitkomst)) return { ok: false, reason: 'ongeldige uitkomst' };
  if (!Array.isArray(data.bevindingen) || data.bevindingen.length > MAX_BEVINDINGEN) {
    return { ok: false, reason: 'bevindingen ontbreekt of is te lang' };
  }
  if (typeof data.samenvatting !== 'string') return { ok: false, reason: 'samenvatting ontbreekt' };
  const bevindingen = [];
  for (const b of data.bevindingen) {
    if (!b || typeof b !== 'object' || !PRIORITEITEN.includes(b.prioriteit)
      || ['locatie', 'reden', 'verificatie'].some((k) => typeof b[k] !== 'string')) {
      return { ok: false, reason: 'een bevinding heeft een ongeldige vorm' };
    }
    bevindingen.push({
      prioriteit: b.prioriteit,
      locatie: schoon(b.locatie, MAX.locatie, { eenRegel: true }),
      reden: schoon(b.reden, MAX.reden),
      verificatie: schoon(b.verificatie, MAX.verificatie),
    });
  }
  if ((data.uitkomst === 'geen bevindingen') !== (bevindingen.length === 0)) {
    return { ok: false, reason: 'uitkomst en aantal bevindingen spreken elkaar tegen' };
  }
  return {
    ok: true,
    data: {
      uitvoerbaar: data.uitvoerbaar,
      besloten: data.besloten_bevinding,
      bevindingen,
      samenvatting: schoon(data.samenvatting, MAX.samenvatting),
    },
  };
}

/** De volgende beurt: Codex, behalve als de review niet uitvoerbaar was of een besloten notitie vraagt. */
function nextTurn(data) {
  return data.uitvoerbaar && !data.besloten ? 'turn: codex' : 'turn: owner';
}

function uitkomstTekst(data) {
  if (!data.uitvoerbaar) return 'review niet uitvoerbaar';
  return data.bevindingen.length ? `${data.bevindingen.length} bevinding(en)` : 'geen bevindingen';
}

function composePrComment({ ronde, sha, data }) {
  const regels = [
    `Reviewteruggave — reviewronde ${ronde}`,
    'Reviewer: Claude Code (CI-run claude-issue-turn.yml, read-only)',
    `Beoordeelde SHA: ${sha}`,
    `Uitkomst: ${uitkomstTekst(data)}`,
    '',
  ];
  data.bevindingen.forEach((b, i) => {
    regels.push(`${i + 1}. **${b.prioriteit}** — \`${b.locatie}\``, '', b.reden, '', `Benodigde verificatie: ${b.verificatie}`, '');
  });
  if (data.besloten) regels.push('Een bevinding vraagt een besloten notitie (SECURITY.md); de eigenaar beslist over de verwerking.', '');
  if (data.samenvatting) regels.push(data.samenvatting, '');
  regels.push(reviewMarker(sha));
  return regels.join('\n');
}

function composeIssueComment({ prNumber, sha, data, beurt }) {
  return [
    'Fase: verwerking',
    `PR: #${prNumber}`,
    'Implementer: Codex',
    'Reviewer: Claude Code (CI-run claude-issue-turn.yml)',
    `Beoordeelde SHA: ${sha}`,
    `Uitkomst: ${uitkomstTekst(data)} (reviewteruggave op de PR)`,
    `Volgende beurt: ${beurt}`,
  ].join('\n');
}

/** Naam van de eerste verboden waarde in de tekst, of null. */
function scanPublicText(text) {
  const hit = VERBODEN.find((v) => v.re.test(text));
  if (hit) return hit.naam;
  const domeinen = [...text.matchAll(EMAIL_RE)].map((m) => m[1]);
  return domeinen.some((d) => !TOEGESTANE_DOMEINEN.test(d)) ? 'e-mailadres' : null;
}

/**
 * Controleert de aanvraag opnieuw tegen de actuele toestand; geeft de reden terug waarom er niet
 * gepubliceerd mag worden, of null. `checkReviewed: false` na de eigen publicatie (dan staat de
 * markering er al).
 */
async function revalidate({ github, context, issueNumber, prNumber, sha, checkReviewed = true }) {
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  const issue = (await github.rest.issues.get({ ...base, issue_number: issueNumber })).data;
  const labels = labelNames(issue);
  if (issue.state !== 'open') return `het issue is niet meer open (state: ${issue.state})`;
  if (labels.includes(STATUS_AWAITING_RELEASE)) return `het issue heeft \`${STATUS_AWAITING_RELEASE}\``;
  const turn = labels.filter((n) => n.startsWith(TURN_LABEL_PREFIX));
  if (turn.length !== 1 || turn[0] !== TURN_LABEL_CLAUDE) return 'de beurt is niet meer exclusief `turn: claude-code`';

  const request = await findReviewRequest({ github, context, issueNumber });
  if (!request || request.prNumber !== prNumber || request.sha !== sha) {
    return 'de reviewaanvraag is ingetrokken of vervangen (nieuwste fasecomment)';
  }
  const linked = await findLinkedOpenPrsByKind({ github, context, issueNumber });
  const all = [...linked.own, ...linked.codex];
  if (all.length !== 1 || !linked.codex[0] || linked.codex[0].number !== prNumber) {
    return `PR #${prNumber} is niet meer de enige gekoppelde open Codex-PR`;
  }
  if ((linked.codex[0].headSha || '').toLowerCase() !== sha) return `de head van PR #${prNumber} is gewijzigd`;
  if (checkReviewed && await isShaAlreadyReviewed({ github, context, prNumber, sha })) {
    return 'de SHA is intussen al beoordeeld';
  }
  return null;
}

/**
 * @returns {Promise<{published: boolean, reason?: string, label?: string, labelWritten?: boolean}>}
 */
async function publishReview({
  github, context, core, issueNumber, prNumber, sha, ronde, raw, agentResult, startedAt,
}) {
  if (agentResult !== 'success') return { published: false, reason: `de review-agent eindigde met '${agentResult}'` };
  const parsed = parseReviewOutput(raw);
  if (!parsed.ok) return { published: false, reason: parsed.reason };
  const { data } = parsed;
  const beurt = nextTurn(data);
  const prBody = composePrComment({ ronde, sha, data });
  const issueBody = composeIssueComment({ prNumber, sha, data, beurt });
  const verboden = scanPublicText(`${prBody}\n${issueBody}`);
  if (verboden) return { published: false, reason: `de tekst bevat mogelijk een niet-publieke waarde (${verboden})` };

  const before = await revalidate({ github, context, issueNumber, prNumber, sha });
  if (before) return { published: false, reason: before };

  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  await github.rest.issues.createComment({ ...base, issue_number: prNumber, body: prBody });
  await github.rest.issues.createComment({ ...base, issue_number: issueNumber, body: issueBody });
  core.notice(`#${issueNumber}: reviewteruggave voor PR #${prNumber} (${sha}) geplaatst`);

  // Nog één controle van de aanvraag; de vergelijk-en-zet bewaakt daarna beurt, PR-status en SHA.
  const after = await revalidate({ github, context, issueNumber, prNumber, sha, checkReviewed: false });
  if (after) {
    core.warning(`#${issueNumber}: beurtlabel niet gezet: ${after}`);
    return { published: true, label: beurt, labelWritten: false, reason: after };
  }
  const r = await applyEndLabel({
    github, context, core, issueNumber, startPrNumber: prNumber, startedAt,
    phase: 'review', requestedLabel: beurt, expectedSha: sha,
  });
  return { published: true, label: r.label, labelWritten: r.written, reason: r.reason };
}

module.exports = {
  publishReview, parseReviewOutput, composePrComment, composeIssueComment, scanPublicText, revalidate,
  nextTurn, REVIEW_SCHEMA,
};
