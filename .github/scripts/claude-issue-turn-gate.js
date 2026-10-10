// claude-issue-turn-gate.js
//
// Deterministische poortstap vóór claude-issue-turn.yml de Claude Code GitHub Action
// aanroept. Issue #1601 besloot dat alleen `turn: claude-code` een CI-run mag starten
// (Codex-automatisering via een abonnementstoken is niet conform OpenAI's eigen
// CI/CD-voorwaarden op deze publieke repo — zie de besluit-2-comment op #1601). Deze
// poort dwingt drie dingen af die een losse `if:` in de workflow niet kan:
//
//  1. Scope-restrictie (besluit 3): alleen issues met een agent-/eigenaarherkomst
//     (`source: claude-code` of `source: owner`) en zonder `discipline: architect`.
//     Een issue met `via: feedback-widget` heeft per AGENTS.md geen `source:`-label
//     en valt dus vanzelf al buiten deze scope — geen aparte uitzondering nodig.
//  2. Lusbeveiliging: een label alleen is geen garantie tegen herhaald (opnieuw)
//     labelen. Bij meer dan MAX_RUNS eerdere runs voor hetzelfde issue stopt de poort
//     en gaat de beurt naar de eigenaar — dezelfde drempel als ">3 iteraties zonder
//     voortgang" in AGENTS.md, "Autonome ontwikkelcyclus".
//  3. Fasedetectie (implementatiestap 3 van #1601): heeft dit issue al een open PR
//     (via een 'strong' referentie, zie extractIssueRefs), dan is de beurt een
//     verwerkingsronde op die branch — anders een verse implementatie vanaf develop.

const { extractIssueRefs } = require('./issue-status.js');

const API_VERSION_HEADERS = { 'x-github-api-version': '2022-11-28' };

const REQUIRED_SOURCE_LABELS = ['source: claude-code', 'source: owner'];
const BLOCKED_LABELS = ['discipline: architect'];

// Zelfde drempel als AGENTS.md, "Escaleer naar gebruiker bij": "> 3 iteraties in
// verificatielus zonder voortgang". Hier: meer dan 3 eerdere runs van déze workflow
// voor hetzelfde issue wijst op thrashing, niet op voortgang.
const MAX_RUNS = 3;

const WORKFLOW_FILE = 'claude-issue-turn.yml';
const TURN_LABEL_PREFIX = 'turn: ';

function labelNames(issue) {
  return (issue.labels || []).map((l) => (typeof l === 'string' ? l : l.name));
}

/** Wisselt het turn:-label met remove+add — nooit los, zie AGENTS.md "turn:-workflow". */
async function setTurnLabel({ github, context, core, issueNumber, label }) {
  const issue = (
    await github.rest.issues.get({
      owner: context.repo.owner,
      repo: context.repo.repo,
      issue_number: issueNumber,
      headers: API_VERSION_HEADERS,
    })
  ).data;

  const stale = labelNames(issue).filter(
    (n) => n.startsWith(TURN_LABEL_PREFIX) && n !== label,
  );

  // Eerst toevoegen, dan de oude verwijderen: bij een falende run blijft er zo altijd
  // minstens één turn-label staan in plaats van geen enkele.
  await github.rest.issues.addLabels({
    owner: context.repo.owner,
    repo: context.repo.repo,
    issue_number: issueNumber,
    labels: [label],
    headers: API_VERSION_HEADERS,
  });

  for (const name of stale) {
    await github.rest.issues
      .removeLabel({
        owner: context.repo.owner,
        repo: context.repo.repo,
        issue_number: issueNumber,
        name,
        headers: API_VERSION_HEADERS,
      })
      .catch(() => {});
  }

  core.notice(`#${issueNumber}: turn-label → '${label}'`);
}

async function postComment({ github, context, core, issueNumber, body }) {
  await github.rest.issues.createComment({
    owner: context.repo.owner,
    repo: context.repo.repo,
    issue_number: issueNumber,
    body,
    headers: API_VERSION_HEADERS,
  });
  core.notice(`#${issueNumber}: toelichting geplaatst`);
}

/** Zoekt een open PR die dit issue 'strong' noemt (titel-haakjes of sluitend keyword). */
async function findLinkedOpenPr({ github, context, issueNumber }) {
  const prs = await github.paginate(github.rest.pulls.list, {
    owner: context.repo.owner,
    repo: context.repo.repo,
    state: 'open',
    per_page: 50,
    headers: API_VERSION_HEADERS,
  });

  for (const pr of prs) {
    const { strong } = extractIssueRefs(pr.title, pr.body);
    if (strong.has(issueNumber)) {
      return { number: pr.number, branch: pr.head.ref };
    }
  }
  return null;
}

/** Telt eerdere runs van déze workflow voor dit issue, de huidige run niet meegerekend. */
async function countPriorRuns({ github, context, issueNumber }) {
  const runs = await github.paginate(github.rest.actions.listWorkflowRuns, {
    owner: context.repo.owner,
    repo: context.repo.repo,
    workflow_id: WORKFLOW_FILE,
    per_page: 50,
    headers: API_VERSION_HEADERS,
  });

  const marker = `#${issueNumber}`;
  return runs.filter(
    (r) => r.id !== context.runId && (r.display_title || '').includes(marker),
  ).length;
}

/**
 * @returns {Promise<{proceed: boolean, phase?: 'implementatie'|'verwerking',
 *   prNumber?: number, prBranch?: string, reason?: string}>}
 */
async function evaluateGate({ github, context, core, issueNumber }) {
  const issue = (
    await github.rest.issues.get({
      owner: context.repo.owner,
      repo: context.repo.repo,
      issue_number: issueNumber,
      headers: API_VERSION_HEADERS,
    })
  ).data;

  const labels = labelNames(issue);

  const hasRequiredSource = REQUIRED_SOURCE_LABELS.some((l) => labels.includes(l));
  if (!hasRequiredSource) {
    const reason =
      `Geen CI-run gestart: dit issue mist een van ${REQUIRED_SOURCE_LABELS.join(' / ')}. ` +
      'Besluit 3 op #1601 beperkt automatische oppak tot issues met een agent- of ' +
      'eigenaarherkomst (bijv. geen `via: feedback-widget`-issues).';
    await postComment({ github, context, core, issueNumber, body: reason });
    await setTurnLabel({ github, context, core, issueNumber, label: 'turn: owner' });
    return { proceed: false, reason };
  }

  const blocked = BLOCKED_LABELS.find((l) => labels.includes(l));
  if (blocked) {
    const reason =
      `Geen CI-run gestart: label \`${blocked}\` vereist een expliciete eigenaarsopdracht ` +
      '(besluit 3 op #1601) — architectuurbeslissingen lopen niet autonoom via deze workflow.';
    await postComment({ github, context, core, issueNumber, body: reason });
    await setTurnLabel({ github, context, core, issueNumber, label: 'turn: owner' });
    return { proceed: false, reason };
  }

  const priorRuns = await countPriorRuns({ github, context, issueNumber });
  if (priorRuns >= MAX_RUNS) {
    const reason =
      `Geen CI-run gestart: al ${priorRuns} eerdere runs van deze workflow op dit issue ` +
      `(drempel ${MAX_RUNS}, zelfde grens als "> 3 iteraties zonder voortgang" in AGENTS.md). ` +
      'Vereist een eigenaarsbesluit vóór een volgende poging.';
    await postComment({ github, context, core, issueNumber, body: reason });
    await setTurnLabel({ github, context, core, issueNumber, label: 'turn: owner' });
    return { proceed: false, reason };
  }

  const linkedPr = await findLinkedOpenPr({ github, context, issueNumber });
  if (linkedPr) {
    return {
      proceed: true,
      phase: 'verwerking',
      prNumber: linkedPr.number,
      prBranch: linkedPr.branch,
    };
  }

  return { proceed: true, phase: 'implementatie' };
}

module.exports = { evaluateGate, setTurnLabel, findLinkedOpenPr, countPriorRuns, MAX_RUNS };
