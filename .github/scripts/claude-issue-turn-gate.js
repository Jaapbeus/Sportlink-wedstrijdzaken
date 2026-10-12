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
//  3a. Actuele beurt (#1664): een wachtende of verlate run start alleen als het issue nu open is
//     en precies één turn:-label heeft, `turn: claude-code`. Meerdere gekoppelde PR's (#1665)
//     stoppen de poort zonder agent of labelwijziging.
//  3b. Al afgerond werk (#1692): een gekoppelde PR die al gemerged is, of `status: awaiting-release`
//     op het issue, stopt de poort zonder agent of labelwijziging. Een interactieve sessie kan het
//     issue intussen hebben afgerond; vervolgwerk na een merge hoort in een nieuw issue.
//  3c. Besloten reviewnotitie (#1686): verwijst de laatste reviewteruggave op de gekoppelde PR naar
//     een besloten notitie, dan kan de agent die bevinding niet inzien. Geen run, beurt naar de eigenaar.
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
const TURN_LABEL_CLAUDE = 'turn: claude-code';
const STATUS_AWAITING_RELEASE = 'status: awaiting-release';

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

// De invoer gaat via een workflow-input naar de action en daar als omgevingsvariabele (PROMPT)
// naar een shellstap. Linux begrenst één string op MAX_ARG_STRLEN = 128 KiB, gemeten in BYTES
// (#1666). De vaste workflowprompt is ~5,2 KiB (gemeten op de tekst in claude-issue-turn.yml);
// de context (issuetitel/-body, headers, separators en comments, alles inbegrepen) blijft daarom
// op ten hoogste 96 KiB, zodat prompt + context ruim onder 128 KiB eindigt.
const MAX_CONTEXT_BYTES = 96 * 1024;

// Aandeel van het budget voor titel + body van het issue; de rest is voor comments en reviews, zodat
// een enorme issuetekst de laatste reviewronde nooit kan verdringen.
const ISSUE_BUDGET_BYTES = Math.floor(MAX_CONTEXT_BYTES / 2);

// Ruimte voor de kopregels, scheidingstekens en afkapmarkeringen (die pas na het afkappen bekend zijn).
const OVERHEAD_RESERVE_BYTES = 1024;

const TRUNCATION_MARKER = '\n\n[… AFGEKAPT wegens omvang: de rest van deze tekst is weggelaten …]';

// #1686: een reviewteruggave noemt de ronde en de uitkomst; een reviewaanvraag of verwerking niet als
// teruggave tellen, ook als die woorden erin staan. De verwijzing dekt 'besloten (lokale) (review)notitie'.
const REVIEW_RONDE = /review[\s-]*ronde/i;
const REVIEW_UITKOMST = /\buitkomst\b/i;
const GEEN_TERUGGAVE = /^[\s*_#>]*(?:verwerking|reviewaanvraag|afronding)/i;
const BESLOTEN_NOTITIE = /besloten[^\n.]{0,60}notitie/i;

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
 * Zoekt álle open PR's van déze beurt (#1665): een 'strong' referentie naar het issue én (#1612) een
 * branch in deze repository met de eigen conventie `feature/#<nr>-`, van een bevoegde auteur.
 * Zonder die drie voorwaarden kan iedereen met een fork-PR `fix(#N): ...` bepalen welke PR
 * de schrijfbevoegde agent als zijn werk behandelt.
 */
function isOwnLinkedPr(pr, { context, issueNumber }) {
  const { strong } = extractIssueRefs(pr.title, pr.body);
  if (!strong.has(issueNumber)) return false;
  if (pr.head?.repo?.full_name !== `${context.repo.owner}/${context.repo.repo}`) return false;
  if (!(pr.head?.ref || '').startsWith(`feature/#${issueNumber}-`)) return false;
  // isTrustedAuthor, niet isTrusted: de PR die de CI-run zelf opent staat op naam van
  // claude[bot] (associatie NONE) en moet in de verwerkingsfase wél herkend worden (#1633).
  return isTrustedAuthor(pr);
}

async function findLinkedOpenPrs({ github, context, issueNumber }) {
  const prs = await github.paginate(github.rest.pulls.list, {
    owner: context.repo.owner,
    repo: context.repo.repo,
    state: 'open',
    per_page: 50,
    headers: API_VERSION_HEADERS,
  });

  const linked = prs
    .filter((pr) => isOwnLinkedPr(pr, { context, issueNumber }))
    .map((pr) => ({ number: pr.number, branch: pr.head.ref }));
  // Vaste volgorde: de uitkomst hangt niet van de API-volgorde af.
  return linked.sort((a, b) => a.number - b.number);
}

const CLOSED_PAGE_SIZE = 100;

/**
 * Gesloten (gemergede of afgewezen) PR's van déze beurt, met dezelfde filters als
 * findLinkedOpenPrs (#1692). Nieuwst bijgewerkt eerst; stopt zodra een pagina PR's bevat die vóór
 * `since` zijn bijgewerkt. Een PR die naar het issue verwijst is altijd ná het aanmaken van het
 * issue gemaakt of bewerkt, dus `since` = aanmaakmoment van het issue mist er geen.
 */
async function findLinkedClosedPrs({ github, context, issueNumber, since }) {
  const linked = [];
  for (let page = 1; ; page += 1) {
    const { data } = await github.rest.pulls.list({
      owner: context.repo.owner,
      repo: context.repo.repo,
      state: 'closed',
      sort: 'updated',
      direction: 'desc',
      per_page: CLOSED_PAGE_SIZE,
      page,
      headers: API_VERSION_HEADERS,
    });
    for (const pr of data) {
      if (since && pr.updated_at && pr.updated_at < since) continue;
      if (!isOwnLinkedPr(pr, { context, issueNumber })) continue;
      linked.push({ number: pr.number, mergedAt: pr.merged_at || null, closedAt: pr.closed_at || null });
    }
    const oldest = data.length ? data[data.length - 1].updated_at : null;
    if (data.length < CLOSED_PAGE_SIZE || (since && oldest && oldest < since)) break;
  }
  return linked.sort((a, b) => a.number - b.number);
}

function isReviewTeruggave(body) {
  return REVIEW_RONDE.test(body) && REVIEW_UITKOMST.test(body) && !GEEN_TERUGGAVE.test(body);
}

/** De nieuwste reviewteruggave van een bevoegde schrijver op de PR (#1686), of null. */
async function findLatestReviewReturn({ github, context, prNumber }) {
  const comments = await github.paginate(github.rest.issues.listComments, {
    owner: context.repo.owner,
    repo: context.repo.repo,
    issue_number: prNumber,
    per_page: 100,
    headers: API_VERSION_HEADERS,
  });
  let latest = null;
  for (const c of comments) {
    if (!c.body || !isTrustedAuthor(c) || !isReviewTeruggave(c.body)) continue;
    if (!latest || (c.created_at || '') >= (latest.created_at || '')) latest = c;
  }
  return latest;
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

function byteLength(text) {
  return Buffer.byteLength(text, 'utf8');
}

/**
 * Kapt af op maximaal `maxBytes` UTF-8-bytes, nooit midden in een teken, en voegt dan een
 * expliciete markering toe (de markering telt mee in de limiet). Past de tekst al: ongewijzigd.
 */
function truncateToBytes(text, maxBytes) {
  if (byteLength(text) <= maxBytes) return text;
  const markerBytes = byteLength(TRUNCATION_MARKER);
  if (maxBytes <= markerBytes) return Buffer.from(TRUNCATION_MARKER, 'utf8').subarray(0, Math.max(0, maxBytes)).toString('utf8');
  const buf = Buffer.from(text, 'utf8');
  let end = maxBytes - markerBytes;
  // Een UTF-8-vervolgbyte is 10xxxxxx: terug tot het begin van een teken.
  while (end > 0 && (buf[end] & 0xc0) === 0x80) end -= 1;
  return buf.subarray(0, end).toString('utf8') + TRUNCATION_MARKER;
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

  // Titel en tekst samen: een auteur kan beide op elk moment wijzigen (#1615).
  const issueTrusted = isTrustedAuthor(issue);
  const issueTitle = issueTrusted ? issue.title : '(titel weggelaten: geen bevoegde auteur)';
  const issueBody = issueTrusted ? issue.body || '(lege issuetekst)' : '(issuetekst weggelaten: geen bevoegde auteur)';
  const issueHead = `## Issue #${issueNumber}: ${issueTitle}`;

  // #1666: alles in UTF-8-bytes. De issuetekst krijgt een eigen deel van het budget.
  const issuePart = truncateToBytes(`${issueHead}\n\n${issueBody}`, ISSUE_BUDGET_BYTES);
  const commentBudget = MAX_CONTEXT_BYTES - OVERHEAD_RESERVE_BYTES - byteLength(issuePart);
  const separator = '\n\n---\n\n';

  // Nieuwste eerst behouden tot de limiet: de laatste reviewronde is wat telt. Past zelfs de
  // nieuwste bijdrage niet, dan wordt die afgekapt in plaats van weggelaten.
  const kept = [];
  let size = 0;
  for (let i = entries.length - 1; i >= 0; i -= 1) {
    const cost = (kept.length ? byteLength(separator) : 0);
    let text = formatEntry(entries[i].kind, entries[i].item);
    const room = commentBudget - size - cost;
    if (byteLength(text) > room) {
      if (kept.length === 0 && room > 0) kept.unshift(truncateToBytes(text, room));
      break;
    }
    kept.unshift(text);
    size += byteLength(text) + cost;
  }
  const truncated = entries.length - kept.length;
  const issueTruncated = issuePart !== `${issueHead}\n\n${issueBody}`;

  const header = [
    `## Comments en reviews van bevoegde schrijvers (${kept.length})`,
    skipped ? `_${skipped} bijdrage(n) van niet-bevoegde schrijvers weggelaten._` : '',
    truncated ? `_${truncated} oudere bijdrage(n) weggelaten wegens omvang._` : '',
    issueTruncated ? '_De issuetekst is afgekapt wegens omvang._' : '',
  ].filter((line) => line !== '').join('\n');

  let text = `${issuePart}\n\n${header}\n\n${kept.join(separator)}`.trim();
  // Vangnet: de som mag nooit boven de limiet uitkomen.
  text = truncateToBytes(text, MAX_CONTEXT_BYTES);

  return { text, skipped, truncated, bytes: byteLength(text) };
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

  // #1664: het label-event waarop deze run startte kan inmiddels achterhaald zijn (de run wachtte
  // in de concurrency-groep, of de eigenaar sloot het issue of nam de beurt terug). Alleen een open
  // issue met precies één turn:-label, en dat is `turn: claude-code`, start een agent. Bewust zonder
  // labelwijziging of heropenen: de actuele toestand is de bedoeling van de eigenaar.
  const turnLabels = labels.filter((n) => n.startsWith(TURN_LABEL_PREFIX));
  if (issue.state !== 'open') {
    return { proceed: false, reason: `Geen CI-run gestart: het issue is niet meer open (state: ${issue.state}).` };
  }
  if (turnLabels.length !== 1 || turnLabels[0] !== TURN_LABEL_CLAUDE) {
    return {
      proceed: false,
      reason:
        `Geen CI-run gestart: de actuele beurt is niet exclusief \`${TURN_LABEL_CLAUDE}\` ` +
        `(huidige turn-labels: ${turnLabels.length ? turnLabels.join(', ') : 'geen'}); verouderde run.`,
    };
  }

  // #1692: een interactieve sessie kan het werk al hebben afgerond terwijl deze run wachtte. Geen
  // agent en geen labelwijziging: de merge-automatisering heeft de beurt al opgeruimd.
  if (labels.includes(STATUS_AWAITING_RELEASE)) {
    return { proceed: false, reason: `Geen CI-run gestart: het issue heeft \`${STATUS_AWAITING_RELEASE}\` (werk al gemerged).` };
  }
  const merged = (await findLinkedClosedPrs({ github, context, issueNumber, since: issue.created_at }))
    .filter((pr) => pr.mergedAt);
  if (merged.length) {
    return {
      proceed: false,
      reason:
        `Geen CI-run gestart: gekoppelde PR ${merged.map((p) => `#${p.number}`).join(', ')} is al gemerged; ` +
        'vervolgwerk hoort in een nieuw issue.',
    };
  }

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

  const linkedPrs = await findLinkedOpenPrs({ github, context, issueNumber });
  if (linkedPrs.length > 1) {
    // #1665: kiezen op API-volgorde zou de verkeerde branch wijzigen; het reviewprotocol vereist
    // stoppen. Bewust geen agent en geen labelwijziging: de beurt blijft zoals de eigenaar hem zette.
    const reason =
      `Geen CI-run gestart: meerdere PR-koppelingen (${linkedPrs.map((p) => `#${p.number}`).join(', ')}), ` +
      'eigenaarsbesluit nodig welke PR bij dit issue hoort.';
    return { proceed: false, reason };
  }
  const linkedPr = linkedPrs[0];
  if (linkedPr) {
    // #1686: algemene bewoording; de inhoud van de bevinding hoort niet in een publiek issue (regel 4a).
    const teruggave = await findLatestReviewReturn({ github, context, prNumber: linkedPr.number });
    if (teruggave && BESLOTEN_NOTITIE.test(teruggave.body)) {
      const reason =
        `Geen CI-run gestart: de laatste reviewteruggave op PR #${linkedPr.number} verwijst naar een besloten ` +
        'reviewnotitie die deze run niet kan inzien; een bevinding verwerken zonder die notitie zou gokken zijn. ' +
        'De eigenaar stelt de notitie beschikbaar of besluit.';
      await postComment({ github, context, core, issueNumber, body: reason });
      await setTurnLabel({ github, context, core, issueNumber, label: 'turn: owner' });
      return { proceed: false, reason };
    }
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
  findLinkedOpenPrs,
  findLinkedClosedPrs,
  isTrustedAuthor,
  labelNames,
  API_VERSION_HEADERS,
  TURN_LABEL_PREFIX,
  TURN_LABEL_CLAUDE,
  STATUS_AWAITING_RELEASE,
  findLatestReviewReturn,
  countPriorRuns,
  collectTrustedContext,
  runTitle,
  MAX_RUNS,
  MAX_CONTEXT_BYTES,
  truncateToBytes,
};
