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
//  3d. Reviewfase (#1727): is de laatste fasecomment op het issue een reviewaanvraag met
//     `Reviewer: Claude Code` en een andere implementer, dan is de beurt een read-only review van
//     precies de aangeboden head-SHA van de Codex-PR. Alleen in deze fase mag het issue
//     `source: codex` hebben. Een aanvraag die niet klopt (andere SHA, al beoordeeld, zelfreview,
//     geen of meerdere PR-koppelingen) start geen agent en wijzigt niets (reviewprotocol in
//     AGENTS.md, regel 3); de poortstap faalt dan zichtbaar. Zonder geldige aanvraag start een
//     issue met een open Codex-PR nooit een implementatie of verwerking: dat zou een tweede
//     implementatie naast die van Codex worden.
//  4. Vertrouwde invoer (#1612): op een publieke repo kan iedereen een PR openen en commentaar
//     plaatsen. Alleen PR's uit deze repo van een bevoegde auteur tellen, en de agent krijgt
//     uitsluitend tekst van bevoegde schrijvers aangereikt (collectTrustedContext).

const { extractIssueRefs } = require('./issue-status.js');

const API_VERSION_HEADERS = { 'x-github-api-version': '2022-11-28' };

const REQUIRED_SOURCE_LABELS = ['source: claude-code', 'source: owner'];
// #1727: Claude Code reviewt ook Codex-werk; dat issue heeft `source: codex`. Alleen in de reviewfase.
const REVIEW_SOURCE_LABELS = ['source: codex'];
const BLOCKED_LABELS = ['discipline: architect'];

// AGENTS.md, "Review en turn:" regel 4: hoogstens twee reviewrondes per PR.
const MAX_REVIEW_ROUNDS = 2;

// Regels van een fasecomment (AGENTS.md, "Leg de opdracht vast bij het issue"). Regelgebonden, met
// ruimte voor opsommingstekens en markdown-nadruk; lopende tekst die "Fase: review" noemt telt niet.
const FIELD_PREFIX = String.raw`^[\s>*_-]*`;
const FASE_RE = new RegExp(`${FIELD_PREFIX}Fase:[\\s*_\`]*([a-z]+)`, 'im');
const REVIEWER_RE = new RegExp(`${FIELD_PREFIX}Reviewer:[\\s*_\`]*(.+)$`, 'im');
const IMPLEMENTER_RE = new RegExp(`${FIELD_PREFIX}Implementer:[\\s*_\`]*(.+)$`, 'im');
const PR_RE = new RegExp(`${FIELD_PREFIX}PR:[\\s*_\`]*#(\\d+)\\b`, 'im');
const SHA_RE = new RegExp(`${FIELD_PREFIX}Aangeboden head-SHA:[\\s*_\`]*([0-9a-f]{40})\\b`, 'im');
const CLAUDE_CODE = /^claude[\s-]*code\b/i;

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

/** Branchsoort van een PR voor dit issue: eigen conventie `feature/#<nr>-` of Codex (`codex/[hotfix-]<nr>-`). */
function branchKind(ref, issueNumber) {
  if ((ref || '').startsWith(`feature/#${issueNumber}-`)) return 'own';
  if (new RegExp(`^codex/(?:hotfix-)?${issueNumber}-`).test(ref || '')) return 'codex';
  return null;
}

/**
 * Een PR van déze beurt (#1665): een 'strong' referentie naar het issue én (#1612) een branch in
 * deze repository met een bekende conventie (eigen of, #1727, Codex), van een bevoegde auteur.
 * Zonder die drie voorwaarden kan iedereen met een fork-PR `fix(#N): ...` bepalen welke PR
 * de schrijfbevoegde agent als zijn werk behandelt. Geeft de branchsoort terug, of null.
 */
function linkedPrKind(pr, { context, issueNumber }) {
  const { strong } = extractIssueRefs(pr.title, pr.body);
  if (!strong.has(issueNumber)) return null;
  if (pr.head?.repo?.full_name !== `${context.repo.owner}/${context.repo.repo}`) return null;
  const kind = branchKind(pr.head?.ref, issueNumber);
  // isTrustedAuthor, niet isTrusted: de PR die de CI-run zelf opent staat op naam van
  // claude[bot] (associatie NONE) en moet in de verwerkingsfase wél herkend worden (#1633).
  return kind && isTrustedAuthor(pr) ? kind : null;
}

/** Alle gekoppelde open PR's, per branchsoort; vaste volgorde, onafhankelijk van de API. */
async function findLinkedOpenPrsByKind({ github, context, issueNumber }) {
  const prs = await github.paginate(github.rest.pulls.list, {
    owner: context.repo.owner,
    repo: context.repo.repo,
    state: 'open',
    per_page: 50,
    headers: API_VERSION_HEADERS,
  });

  const result = { own: [], codex: [] };
  for (const pr of prs) {
    const kind = linkedPrKind(pr, { context, issueNumber });
    if (kind) result[kind].push({ number: pr.number, branch: pr.head.ref, headSha: pr.head.sha });
  }
  result.own.sort((a, b) => a.number - b.number);
  result.codex.sort((a, b) => a.number - b.number);
  return result;
}

/** Open PR's op de eigen conventie `feature/#<nr>-` (implementatie- en verwerkingsfase). */
async function findLinkedOpenPrs(args) {
  return (await findLinkedOpenPrsByKind(args)).own;
}

const CLOSED_PAGE_SIZE = 100;

/**
 * Gesloten (gemergede of afgewezen) PR's van déze beurt, met dezelfde filters als
 * findLinkedOpenPrsByKind (#1692; beide branchsoorten sinds #1727). Nieuwst bijgewerkt eerst; stopt zodra een pagina PR's bevat die vóór
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
      // #1727: ook een gemergede Codex-PR betekent dat het werk af is.
      if (!linkedPrKind(pr, { context, issueNumber })) continue;
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

/** Velden van een fasecomment, of null als er geen `Fase:`-regel in staat. */
function parseFaseComment(body) {
  const fase = (body || '').match(FASE_RE);
  if (!fase) return null;
  const field = (re) => ((body.match(re) || [])[1] || '').replace(/[*_`]+$/, '').trim();
  const pr = Number(field(PR_RE));
  return {
    fase: fase[1].toLowerCase(),
    reviewer: field(REVIEWER_RE),
    implementer: field(IMPLEMENTER_RE),
    prNumber: Number.isInteger(pr) && pr > 0 ? pr : null,
    sha: field(SHA_RE).toLowerCase(),
  };
}

function isClaudeReviewRequest(parsed) {
  return Boolean(parsed) && parsed.fase === 'review' && CLAUDE_CODE.test(parsed.reviewer);
}

/**
 * #1727: de reviewaanvraag aan Claude Code als de nieuwste fasecomment van een bevoegde schrijver
 * er een is, anders null. Een latere fasecomment (bijv. `Fase: verwerking` na de teruggave of een
 * overdracht) beëindigt de aanvraag. `rondes` = het aantal verschillende aangeboden SHA's in
 * reviewaanvragen aan Claude Code voor dezelfde PR.
 */
async function findReviewRequest({ github, context, issueNumber }) {
  const comments = await github.paginate(github.rest.issues.listComments, {
    owner: context.repo.owner,
    repo: context.repo.repo,
    issue_number: issueNumber,
    per_page: 100,
    headers: API_VERSION_HEADERS,
  });
  const faseComments = comments
    .filter((c) => c.body && isTrustedAuthor(c))
    .map((c) => ({ at: c.created_at || '', parsed: parseFaseComment(c.body) }))
    .filter((c) => c.parsed)
    .sort((a, b) => (a.at < b.at ? -1 : a.at > b.at ? 1 : 0));
  const latest = faseComments[faseComments.length - 1];
  if (!latest || !isClaudeReviewRequest(latest.parsed)) return null;

  const shas = new Set(faseComments
    .filter((c) => isClaudeReviewRequest(c.parsed) && c.parsed.prNumber === latest.parsed.prNumber && c.parsed.sha)
    .map((c) => c.parsed.sha));
  return { ...latest.parsed, rondes: shas.size };
}

/** Een reviewteruggave van een bevoegde schrijver op de PR die deze SHA noemt (eenmaal per SHA). */
async function isShaAlreadyReviewed({ github, context, prNumber, sha }) {
  const comments = await github.paginate(github.rest.issues.listComments, {
    owner: context.repo.owner,
    repo: context.repo.repo,
    issue_number: prNumber,
    per_page: 100,
    headers: API_VERSION_HEADERS,
  });
  return comments.some((c) => c.body && isTrustedAuthor(c) && isReviewTeruggave(c.body)
    && c.body.toLowerCase().includes(sha));
}

/**
 * #1727: controleert een reviewaanvraag tegen de actuele toestand. Elke afwijking van het
 * reviewprotocol (AGENTS.md, "Review en turn:" regel 3) stopt zonder comment of labelwijziging en
 * laat de poortstap zichtbaar falen (`alarm`); alleen de rondelimiet gaat met uitleg naar de eigenaar.
 */
async function evaluateReviewRequest({ github, context, core, issueNumber, request }) {
  const stop = (why) => ({ proceed: false, alarm: true, reason: `Geen review gestart: ${why}.` });

  if (!request.implementer || CLAUDE_CODE.test(request.implementer)) {
    return stop('de implementer ontbreekt of is Claude Code zelf (geen zelfreview)');
  }
  if (!request.prNumber || !request.sha) {
    return stop('de reviewaanvraag noemt geen `PR: #<nr>` of geen volledige `Aangeboden head-SHA`');
  }

  const linked = await findLinkedOpenPrsByKind({ github, context, issueNumber });
  const all = [...linked.own, ...linked.codex];
  if (all.length !== 1) {
    const lijst = all.length ? all.map((p) => `#${p.number}`).join(', ') : 'geen';
    return stop(`precies één gekoppelde open PR vereist (gevonden: ${lijst})`);
  }
  const pr = linked.codex[0];
  if (!pr || pr.number !== request.prNumber) {
    return stop(`PR #${request.prNumber} uit de aanvraag is niet de gekoppelde open Codex-PR van dit issue`);
  }
  if ((pr.headSha || '').toLowerCase() !== request.sha) {
    return stop(`de head van PR #${pr.number} is niet meer de aangeboden SHA (nieuwe aanvraag nodig)`);
  }
  if (await isShaAlreadyReviewed({ github, context, prNumber: pr.number, sha: request.sha })) {
    return stop(`de aangeboden SHA van PR #${pr.number} is al beoordeeld (eenmaal per SHA)`);
  }
  if (request.rondes > MAX_REVIEW_ROUNDS) {
    const reason =
      `Geen review gestart: dit is reviewronde ${request.rondes} op PR #${pr.number}; ` +
      `na ${MAX_REVIEW_ROUNDS} rondes zonder schone review beslist de eigenaar (fase \`eigenaarsbesluit\`, AGENTS.md).`;
    await postComment({ github, context, core, issueNumber, body: reason });
    await setTurnLabel({ github, context, core, issueNumber, label: 'turn: owner' });
    return { proceed: false, reason };
  }

  return {
    proceed: true,
    phase: 'review',
    prNumber: pr.number,
    prBranch: pr.branch,
    reviewSha: request.sha,
    ronde: request.rondes,
  };
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

  // #1727: een reviewaanvraag aan Claude Code bepaalt de fase vóór de herkomstcontrole, omdat
  // `source: codex` alleen voor een review is toegestaan.
  const reviewRequest = await findReviewRequest({ github, context, issueNumber });
  const allowedSources = reviewRequest ? [...REQUIRED_SOURCE_LABELS, ...REVIEW_SOURCE_LABELS] : REQUIRED_SOURCE_LABELS;
  const hasRequiredSource = allowedSources.some((l) => labels.includes(l));
  if (!hasRequiredSource) {
    const reason =
      `Geen CI-run gestart: dit issue mist een van ${allowedSources.join(' / ')}. ` +
      'Besluit 3 op #1601 beperkt automatische oppak tot issues met een agent- of ' +
      'eigenaarherkomst (bijv. geen `via: feedback-widget`-issues); `source: codex` alleen voor een ' +
      'review door Claude Code met een geldige reviewaanvraag (#1727).';
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

  if (reviewRequest) {
    return evaluateReviewRequest({ github, context, core, issueNumber, request: reviewRequest });
  }

  const linkedByKind = await findLinkedOpenPrsByKind({ github, context, issueNumber });
  if (linkedByKind.codex.length) {
    // #1727: Codex is hier implementer. Zonder geldige reviewaanvraag zou de bot anders een
    // tweede implementatie vanaf develop starten (zijn eigen branchconventie ziet die PR niet).
    const reason =
      `Geen CI-run gestart: Codex-PR ${linkedByKind.codex.map((p) => `#${p.number}`).join(', ')} hoort bij dit ` +
      'issue en er staat geen geldige reviewaanvraag aan Claude Code als laatste fasecomment ' +
      '(`Fase: review`, `Reviewer: Claude Code`, `PR: #<nr>`, `Aangeboden head-SHA`). ' +
      'Implementatie of verwerking door de bot zou dubbel werk naast de Codex-PR geven.';
    await postComment({ github, context, core, issueNumber, body: reason });
    await setTurnLabel({ github, context, core, issueNumber, label: 'turn: owner' });
    return { proceed: false, reason };
  }

  const linkedPrs = linkedByKind.own;
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
  findLinkedOpenPrsByKind,
  findReviewRequest,
  parseFaseComment,
  MAX_REVIEW_ROUNDS,
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
