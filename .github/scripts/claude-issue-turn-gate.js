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
//  4. Vertrouwde invoer (#1612): op een publieke repo kan iedereen een PR openen en commentaar
//     plaatsen. Alleen PR's uit deze repo van een bevoegde auteur tellen, en de agent krijgt
//     uitsluitend tekst van bevoegde schrijvers aangereikt (collectTrustedContext).

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

// Moet exact gelijk zijn aan de `run-name` in claude-issue-turn.yml voor een turn-label.
const RUN_TITLE_PREFIX = 'Claude issue-beurt #';

// author_association-waarden met schrijfrechten op deze repository. Codex-reviews komen onder
// het account van de eigenaar binnen en vallen daar dus onder; NONE/CONTRIBUTOR/
// FIRST_TIME_CONTRIBUTOR (iedereen op een publieke repo) niet.
const TRUSTED_ASSOCIATIONS = ['OWNER', 'MEMBER', 'COLLABORATOR'];

// Reviewaanvragen en verwerkingscomments uit de CI-run zelf staan op naam van de Claude GitHub
// App en hebben geen OWNER-associatie. Zonder deze uitzondering kan de volgende run de rondes
// niet tellen. Een login met '[bot]' is voorbehouden aan GitHub Apps en dus niet na te bootsen.
const TRUSTED_BOTS = ['claude[bot]'];

// De invoer gaat via een workflow-input naar de action en daar als omgevingsvariabele naar
// het proces. Linux begrenst één variabele op 128 KiB; ruim daaronder blijven.
const MAX_CONTEXT_CHARS = 60000;

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

/** Schrijfbevoegd volgens GitHub; iedereen anders kan op een publieke repo meeschrijven. */
function isTrusted(authorAssociation) {
  return TRUSTED_ASSOCIATIONS.includes(authorAssociation);
}

function isTrustedAuthor(item) {
  if (isTrusted(item.author_association)) return true;
  return item.user?.type === 'Bot' && TRUSTED_BOTS.includes(item.user?.login);
}

/**
 * Zoekt de open PR van déze beurt: een 'strong' referentie naar het issue én (#1612) een
 * branch in deze repository met de eigen conventie `feature/#<nr>-`, van een bevoegde auteur.
 * Zonder die drie voorwaarden kan iedereen met een fork-PR `fix(#N): ...` bepalen welke PR
 * de schrijfbevoegde agent als zijn werk behandelt.
 */
async function findLinkedOpenPr({ github, context, issueNumber }) {
  const prs = await github.paginate(github.rest.pulls.list, {
    owner: context.repo.owner,
    repo: context.repo.repo,
    state: 'open',
    per_page: 50,
    headers: API_VERSION_HEADERS,
  });

  const ownRepo = `${context.repo.owner}/${context.repo.repo}`;
  const branchPrefix = `feature/#${issueNumber}-`;

  for (const pr of prs) {
    const { strong } = extractIssueRefs(pr.title, pr.body);
    if (!strong.has(issueNumber)) continue;
    if (pr.head?.repo?.full_name !== ownRepo) continue;
    if (!(pr.head.ref || '').startsWith(branchPrefix)) continue;
    // isTrustedAuthor, niet isTrusted: de PR die de CI-run zelf opent staat op naam van
    // claude[bot] (associatie NONE) en moet in de verwerkingsfase wél herkend worden (#1633).
    if (!isTrustedAuthor(pr)) continue;
    return { number: pr.number, branch: pr.head.ref };
  }
  return null;
}

/** De run-name van claude-issue-turn.yml — beide plekken moeten exact gelijk blijven. */
function runTitle(issueNumber) {
  return `${RUN_TITLE_PREFIX}${issueNumber}`;
}

/**
 * Telt eerdere runs van déze workflow voor dit issue, de huidige run niet meegerekend.
 * Exacte titelvergelijking: '#4' mag niet '#44' tellen. Runs voor een ánder label op hetzelfde
 * issue krijgen in de workflow een andere run-name en tellen dus ook niet mee.
 */
async function countPriorRuns({ github, context, issueNumber }) {
  const runs = await github.paginate(github.rest.actions.listWorkflowRuns, {
    owner: context.repo.owner,
    repo: context.repo.repo,
    workflow_id: WORKFLOW_FILE,
    per_page: 50,
    headers: API_VERSION_HEADERS,
  });

  const title = runTitle(issueNumber);
  return runs.filter((r) => r.id !== context.runId && r.display_title === title).length;
}

function formatEntry(kind, item) {
  const when = item.submitted_at || item.created_at || '';
  const where = item.path ? ` — ${item.path}${item.line ? `:${item.line}` : ''}` : '';
  return `### ${kind} van @${item.user?.login || 'onbekend'} (${when})${where}\n\n${item.body}`;
}

/**
 * Bouwt de invoer voor de agent uit uitsluitend bevoegde bronnen (#1612): de issuetekst en
 * de comments, plus bij een gekoppelde PR de PR-comments, reviews en inline reviewcomments.
 * Alles van niet-bevoegde schrijvers valt weg en wordt alleen geteld. De agent haalt zelf geen
 * comments meer op, dus dit is de enige weg waarlangs tekst van derden hem kan bereiken.
 */
async function collectTrustedContext({ github, context, issueNumber, prNumber }) {
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };

  const issue = (await github.rest.issues.get({ ...base, issue_number: issueNumber })).data;
  const entries = [];
  let skipped = 0;

  const add = (kind, item) => {
    if (!item.body || !item.body.trim()) return;
    if (isTrustedAuthor(item)) entries.push({ kind, item });
    else skipped += 1;
  };

  const issueComments = await github.paginate(github.rest.issues.listComments, {
    ...base, issue_number: issueNumber, per_page: 100,
  });
  issueComments.forEach((c) => add('Issuecomment', c));

  if (prNumber) {
    const prComments = await github.paginate(github.rest.issues.listComments, {
      ...base, issue_number: prNumber, per_page: 100,
    });
    prComments.forEach((c) => add('PR-comment', c));

    const reviews = await github.paginate(github.rest.pulls.listReviews, {
      ...base, pull_number: prNumber, per_page: 100,
    });
    reviews.forEach((r) => add('Review', r));

    const inline = await github.paginate(github.rest.pulls.listReviewComments, {
      ...base, pull_number: prNumber, per_page: 100,
    });
    inline.forEach((c) => add('Inline reviewcomment', c));
  }

  const byTime = (e) => e.item.submitted_at || e.item.created_at || '';
  entries.sort((a, b) => (byTime(a) < byTime(b) ? -1 : byTime(a) > byTime(b) ? 1 : 0));

  // Nieuwste eerst behouden tot de limiet: de laatste reviewronde is wat telt.
  const kept = [];
  let size = 0;
  for (let i = entries.length - 1; i >= 0; i -= 1) {
    const text = formatEntry(entries[i].kind, entries[i].item);
    if (size + text.length > MAX_CONTEXT_CHARS) break;
    kept.unshift(text);
    size += text.length;
  }
  const truncated = entries.length - kept.length;

  // Titel en tekst samen: een auteur kan beide op elk moment wijzigen (#1615).
  const issueTrusted = isTrustedAuthor(issue);
  const header = [
    `## Issue #${issueNumber}: ${issueTrusted ? issue.title : '(titel weggelaten: geen bevoegde auteur)'}`,
    '',
    issueTrusted ? issue.body || '(lege issuetekst)' : '(issuetekst weggelaten: geen bevoegde auteur)',
    '',
    `## Comments en reviews van bevoegde schrijvers (${kept.length})`,
    skipped ? `_${skipped} bijdrage(n) van niet-bevoegde schrijvers weggelaten._` : '',
    truncated ? `_${truncated} oudere bijdrage(n) weggelaten wegens omvang._` : '',
  ].filter((line) => line !== '').join('\n');

  return { text: `${header}\n\n${kept.join('\n\n---\n\n')}`.trim(), skipped, truncated };
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

  // #1615: een `source:`-label wordt door een maintainer gezet, maar titel en tekst blijven van
  // de auteur, die ze op elk moment kan wijzigen, ook ná het labelen. Een issue van iemand
  // zonder schrijfrechten start daarom nooit een run, ongeacht de labels.
  if (!isTrustedAuthor(issue)) {
    const reason =
      'Geen CI-run gestart: de auteur van dit issue heeft geen schrijfrechten op deze ' +
      'repository (#1615). Een eigenaar kan het werk overnemen in een eigen issue.';
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

module.exports = {
  evaluateGate,
  setTurnLabel,
  findLinkedOpenPr,
  countPriorRuns,
  collectTrustedContext,
  runTitle,
  MAX_RUNS,
  MAX_CONTEXT_CHARS,
};
