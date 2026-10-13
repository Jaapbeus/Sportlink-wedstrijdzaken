// claude-issue-turn-ledger.js
//
// Rondeteller op ID's voor de issue-beurt (#1721, plan v4 §C), fail-closed. Vervangt op termijn het
// tellen van runs op hun titel (`countPriorRuns` in de poort); in fase 1 draait dit alleen in de
// testmodus (`dry_run`) tegen een aparte testledger. Het productiepad telt nog op de oude manier.
//
// Vorm: één comment per issue van `github-actions[bot]` met een marker en een JSON-blok
// `{entries:[{run, run_attempt, created_at, ended_at}], reset_comment_id}`. Het comment wordt ter
// plekke bijgewerkt. Productie: marker `v1`; testmodus: marker `v1-test` (raakt de productieteller nooit).
//
// Bewijs per entry (sleutel = `run` + `run_attempt`): de run moet zelf aantonen dat hij bij dit issue hoort.
//   - `GET /actions/runs/{id}/attempts/{n}`: dezelfde workflow (numeriek id en pad) als de huidige run,
//     het verwachte event, een `triggering_actor` en een oorspronkelijke `actor` van het type User met
//     schrijfrecht (de herstarter wordt dus apart van de oorspronkelijke actor gecontroleerd), in
//     productie de default branch;
//   - de jobs van díe poging: de poortjob (vaste naam, geslaagd, zelfde head-SHA) draagt precies één
//     annotatie `beurt:v1 issue=N run=R attempt=A` (check-run-annotaties van die job).
//
// Elke twijfel stopt de beurt (geen agent, `turn: owner`): ontbrekende ledger terwijl er sporen van
// eerdere beurten zijn (productie), meer dan één ledger, een parsefout, een entry die niet te
// verifiëren is, een dubbele sleutel, een mislukte write. Een API-fout (403/5xx) laat de job falen
// zonder agent en zonder labelwijziging (de fout propageert). Een reset kan alleen via een comment
// `Ledger-reset: <reden>` van de eigenaar (author_association OWNER), nieuwer dan de laatste
// ledgerupdate en nog niet gebruikt (`reset_comment_id`); een lopende beurt kan niet worden gereset.

const API_VERSION_HEADERS = { 'x-github-api-version': '2022-11-28' };

const MAX_RUNS = 3;
const LEDGER_BOT_LOGIN = 'github-actions[bot]';

const MODES = {
  prod: {
    marker: '<!-- claude-issue-turn-ledger v1 -->',
    events: ['issues'],
    requireDefaultBranch: true,
    traceRule: true,
  },
  test: {
    marker: '<!-- claude-issue-turn-ledger v1-test -->',
    events: ['workflow_dispatch'],
    requireDefaultBranch: false,
    traceRule: false,
  },
};

// Namen van de poortjob: direct (productiepad) of via de aanroep-job `beurt` in de router.
const GATE_JOB_NAMES = [
  'Poort — scope, rondelimiet en fase',
  'beurt / Poort — scope, rondelimiet en fase',
];

const ANNOTATION_RE = /^beurt:v1 issue=(\d+) run=(\d+) attempt=(\d+)$/;
const RESET_RE = /^\s*Ledger-reset:\s*\S/;
const TRACE_FASE_RE = /^[\s>*_-]*Fase:\s*\S/im;

class LedgerError extends Error {}

/** Annotatietekst die de poortjob schrijft (en waarop het bewijs steunt). */
function annotationText({ issueNumber, runId, runAttempt }) {
  return `beurt:v1 issue=${issueNumber} run=${runId} attempt=${runAttempt}`;
}

function isInt(v) {
  return Number.isInteger(v) && v > 0;
}

function entryKey(e) {
  return `${e.run}#${e.run_attempt}`;
}

/** Strikte parse van de commenttekst. Gooit LedgerError bij elke afwijking. */
function parseLedger(body, marker) {
  if (typeof body !== 'string' || !body.startsWith(marker)) throw new LedgerError('marker ontbreekt');
  const m = body.slice(marker.length).match(/^\s*```json\n([\s\S]*?)\n```\s*$/);
  if (!m) throw new LedgerError('geen geldig JSON-blok');
  let data;
  try {
    data = JSON.parse(m[1]);
  } catch {
    throw new LedgerError('JSON onleesbaar');
  }
  if (!data || typeof data !== 'object' || Array.isArray(data)) throw new LedgerError('onverwachte vorm');
  const { entries, reset_comment_id: reset } = data;
  if (!Array.isArray(entries)) throw new LedgerError('entries is geen lijst');
  if (!(reset === null || Number.isInteger(reset))) throw new LedgerError('reset_comment_id ongeldig');
  const seen = new Set();
  for (const e of entries) {
    if (!e || typeof e !== 'object' || !isInt(e.run) || !isInt(e.run_attempt)
      || typeof e.created_at !== 'string' || Number.isNaN(Date.parse(e.created_at))
      || !(e.ended_at === null || (typeof e.ended_at === 'string' && !Number.isNaN(Date.parse(e.ended_at))))) {
      throw new LedgerError('ongeldige entry');
    }
    if (seen.has(entryKey(e))) throw new LedgerError('dubbele entry');
    seen.add(entryKey(e));
  }
  return {
    entries: entries.map((e) => ({
      run: e.run, run_attempt: e.run_attempt, created_at: e.created_at, ended_at: e.ended_at,
    })),
    reset_comment_id: reset,
  };
}

function serializeLedger(ledger, marker) {
  const json = JSON.stringify({ entries: ledger.entries, reset_comment_id: ledger.reset_comment_id ?? null }, null, 2);
  return `${marker}\n\`\`\`json\n${json}\n\`\`\``;
}

/** Een comment van de ledgerbot met deze marker; comments van anderen met dezelfde marker tellen niet. */
function isLedgerComment(c, marker) {
  return Boolean(c) && c.user?.type === 'Bot' && c.user?.login === LEDGER_BOT_LOGIN
    && typeof c.body === 'string' && c.body.startsWith(marker);
}

function isOwnerReset(c) {
  return Boolean(c) && c.author_association === 'OWNER' && c.user?.type === 'User'
    && typeof c.body === 'string' && RESET_RE.test(c.body);
}

/** Sporen van eerdere beurten: comments van de Claude App of automatische `Fase:`-comments. */
function hasTraces(comments) {
  return comments.some((c) => c.user?.type === 'Bot'
    && (c.user?.login === 'claude[bot]' || (c.user?.login === LEDGER_BOT_LOGIN && TRACE_FASE_RE.test(c.body || ''))));
}

function iso(v) {
  return new Date(v).getTime();
}

/** Een 404 op een bewijsstuk is "niet te verifiëren"; elke andere fout propageert (job wordt rood). */
async function orMissing(promise) {
  try {
    return await promise;
  } catch (err) {
    if (err?.status === 404) return null;
    throw err;
  }
}

async function hasWriteAccess({ github, base, username }) {
  const res = await orMissing(github.rest.repos.getCollaboratorPermissionLevel({ ...base, username }));
  return Boolean(res) && ['admin', 'write'].includes(res.data?.permission);
}

function pathOf(run) {
  return String(run?.path || '').split('@')[0];
}

/**
 * Bewijs voor één entry (zie bovenaan). Geeft { ok, reason }. `own` = de gegevens van de huidige run
 * (workflow_id en pad) waartegen de herkomst wordt vergeleken.
 */
async function verifyEntry({ github, context, entry, issueNumber, mode, own, defaultBranch }) {
  const cfg = MODES[mode];
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  const fail = (reason) => ({ ok: false, reason: `run ${entry.run} poging ${entry.run_attempt}: ${reason}` });

  const run = (await orMissing(github.rest.actions.getWorkflowRunAttempt({
    ...base, run_id: entry.run, attempt_number: entry.run_attempt,
  })))?.data;
  if (!run) return fail('run of poging niet te vinden');
  if (run.id !== entry.run || run.run_attempt !== entry.run_attempt) return fail('run of poging komt niet overeen');
  if (run.workflow_id !== own.workflow_id || pathOf(run) !== own.path) return fail('andere workflow dan de huidige run');
  if (!cfg.events.includes(run.event)) return fail(`onverwacht event '${run.event}'`);
  if (cfg.requireDefaultBranch && run.head_branch !== defaultBranch) return fail('niet gestart op de default branch');

  const actors = [run.triggering_actor, run.actor].filter(Boolean);
  if (!run.triggering_actor || !run.actor) return fail('actor ontbreekt');
  const logins = new Set();
  for (const a of actors) {
    if (a.type !== 'User') return fail('actor is geen gebruiker');
    if (logins.has(a.login)) continue;
    logins.add(a.login);
    if (!(await hasWriteAccess({ github, base, username: a.login }))) return fail('actor heeft geen schrijfrecht');
  }

  const jobs = await github.paginate(github.rest.actions.listJobsForWorkflowRunAttempt, {
    ...base, run_id: entry.run, attempt_number: entry.run_attempt, per_page: 100,
  });
  const gates = jobs.filter((j) => GATE_JOB_NAMES.includes(j.name));
  if (gates.length !== 1) return fail(`${gates.length} poortjobs gevonden (precies één verwacht)`);
  const gate = gates[0];
  if (gate.conclusion !== 'success' || gate.head_sha !== run.head_sha
    || (gate.run_attempt !== undefined && gate.run_attempt !== entry.run_attempt)) {
    return fail('poortjob niet geslaagd of niet van deze poging');
  }

  const annotations = await github.paginate(github.rest.checks.listAnnotations, {
    ...base, check_run_id: gate.id, per_page: 50,
  });
  const marks = annotations.filter((a) => typeof a.message === 'string' && a.message.startsWith('beurt:v1'));
  if (marks.length !== 1) return fail(`${marks.length} beurtannotaties (precies één verwacht)`);
  const m = marks[0].message.match(ANNOTATION_RE);
  if (!m || Number(m[1]) !== issueNumber || Number(m[2]) !== entry.run || Number(m[3]) !== entry.run_attempt) {
    return fail('annotatie hoort niet bij dit issue, deze run of deze poging');
  }
  return { ok: true };
}

/** Eigen run: workflow-id, pad en aanmaakmoment (referentie voor herkomst en veroudering). */
async function loadOwnRun({ github, context, runId, runAttempt }) {
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  const res = await github.rest.actions.getWorkflowRunAttempt({ ...base, run_id: runId, attempt_number: runAttempt });
  const run = res.data;
  return { workflow_id: run.workflow_id, path: pathOf(run), created_at: run.created_at };
}

async function listIssueComments({ github, context, issueNumber }) {
  return github.paginate(github.rest.issues.listComments, {
    owner: context.repo.owner, repo: context.repo.repo, issue_number: issueNumber, per_page: 100,
    headers: API_VERSION_HEADERS,
  });
}

/**
 * Leest en beoordeelt de ledger. Geeft `{ state: 'stop', reason }` (de beurt stopt, geen agent) of
 * `{ state: 'ok', ledger, commentId, prior, own, reset }`. Een API-fout propageert.
 * @param {{github:object, context:object, issueNumber:number, mode:'prod'|'test', runId:number,
 *   runAttempt:number, defaultBranch?:string, now?:()=>string}} args
 */
async function evaluateLedger({ github, context, issueNumber, mode, runId, runAttempt, defaultBranch = '' }) {
  const cfg = MODES[mode];
  if (!cfg) throw new Error(`onbekende ledgermodus '${mode}'`);
  const stop = (reason) => ({ state: 'stop', reason });
  const comments = await listIssueComments({ github, context, issueNumber });

  const ledgerComments = comments.filter((c) => isLedgerComment(c, cfg.marker));
  if (ledgerComments.length > 1) return stop('meer dan één ledger op dit issue');

  const own = await loadOwnRun({ github, context, runId, runAttempt });
  const resets = comments.filter(isOwnerReset);
  const ledgerComment = ledgerComments[0] || null;

  let ledger;
  let reset = null;
  if (!ledgerComment) {
    if (cfg.traceRule && hasTraces(comments)) {
      const unused = resets[resets.length - 1];
      if (!unused) return stop('geen ledger terwijl er sporen van eerdere beurten zijn (alleen een `Ledger-reset:` van de eigenaar heropent dit)');
      reset = unused;
    }
    ledger = { entries: [], reset_comment_id: reset ? reset.id : null };
  } else {
    try {
      ledger = parseLedger(ledgerComment.body, cfg.marker);
    } catch (err) {
      if (err instanceof LedgerError) return stop(`ledger onleesbaar: ${err.message}`);
      throw err;
    }
    const ledgerAt = iso(ledgerComment.updated_at || ledgerComment.created_at);
    const newer = resets.filter((c) => c.id !== ledger.reset_comment_id && iso(c.created_at) > ledgerAt);
    if (newer.length) reset = newer[newer.length - 1];
  }

  if (reset) {
    // Een lopende beurt kan niet worden gereset (de poort van die beurt rekent op zijn entry).
    for (const e of ledger.entries.filter((x) => x.ended_at === null)) {
      const r = await orMissing(github.rest.actions.getWorkflowRunAttempt({
        owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS,
        run_id: e.run, attempt_number: e.run_attempt,
      }));
      if (r && r.data.status !== 'completed') return stop('reset geweigerd: er loopt nog een beurt');
    }
    ledger = { entries: [], reset_comment_id: reset.id };
  } else {
    for (const e of ledger.entries) {
      if (e.run === runId && e.run_attempt === runAttempt) continue; // eigen entry: poort schrijft hem pas hierna
      const v = await verifyEntry({ github, context, entry: e, issueNumber, mode, own, defaultBranch });
      if (!v.ok) return stop(`ledgerentry niet te verifiëren (${v.reason})`);
    }
  }

  // Verouderingsregel: een run die vóór het einde van de laatste afgeronde beurt is aangemaakt stopt.
  const lastEnded = ledger.entries.filter((e) => e.ended_at).map((e) => iso(e.ended_at)).sort((a, b) => a - b).pop();
  if (lastEnded !== undefined && own.created_at && iso(own.created_at) < lastEnded) {
    return stop('verouderde run: aangemaakt vóór het einde van de laatste beurt');
  }

  const prior = ledger.entries.filter((e) => !(e.run === runId && e.run_attempt === runAttempt)).length;
  if (prior >= MAX_RUNS) {
    return stop(`al ${prior} beurten in de ledger (drempel ${MAX_RUNS}); een eigenaarsbesluit of \`Ledger-reset:\` is nodig`);
  }
  return { state: 'ok', ledger, commentId: ledgerComment ? ledgerComment.id : null, prior, own, reset: Boolean(reset) };
}

/**
 * Schrijft de entry van de huidige poging (idempotent: een bestaande sleutel wordt niet verdubbeld).
 * Geeft `{ state: 'ok' }` of `{ state: 'stop', reason }` bij een mislukte write.
 */
async function recordEntry({ github, context, issueNumber, mode, evaluated, runId, runAttempt, now = () => new Date().toISOString() }) {
  const cfg = MODES[mode];
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  const entries = evaluated.ledger.entries.slice();
  if (!entries.some((e) => e.run === runId && e.run_attempt === runAttempt)) {
    entries.push({ run: runId, run_attempt: runAttempt, created_at: now(), ended_at: null });
  }
  const body = serializeLedger({ entries, reset_comment_id: evaluated.ledger.reset_comment_id }, cfg.marker);
  try {
    if (evaluated.commentId) {
      await github.rest.issues.updateComment({ ...base, comment_id: evaluated.commentId, body });
    } else {
      await github.rest.issues.createComment({ ...base, issue_number: issueNumber, body });
    }
  } catch (err) {
    return { state: 'stop', reason: `ledger schrijven mislukt (${err?.status || 'fout'})` };
  }
  return { state: 'ok', entries };
}

/** Vult `ended_at` van de eigen entry in (door de eindlabeljob; in fase 1 nog niet aangeroepen). */
async function markEnded({ github, context, issueNumber, mode, runId, runAttempt, now = () => new Date().toISOString() }) {
  const cfg = MODES[mode];
  const base = { owner: context.repo.owner, repo: context.repo.repo, headers: API_VERSION_HEADERS };
  const comments = await listIssueComments({ github, context, issueNumber });
  const found = comments.filter((c) => isLedgerComment(c, cfg.marker));
  if (found.length !== 1) return { state: 'stop', reason: `${found.length} ledgers gevonden` };
  let ledger;
  try {
    ledger = parseLedger(found[0].body, cfg.marker);
  } catch (err) {
    if (err instanceof LedgerError) return { state: 'stop', reason: `ledger onleesbaar: ${err.message}` };
    throw err;
  }
  const entry = ledger.entries.find((e) => e.run === runId && e.run_attempt === runAttempt);
  if (!entry) return { state: 'stop', reason: 'eigen entry ontbreekt' };
  if (entry.ended_at) return { state: 'ok', unchanged: true };
  entry.ended_at = now();
  try {
    await github.rest.issues.updateComment({ ...base, comment_id: found[0].id, body: serializeLedger(ledger, cfg.marker) });
  } catch (err) {
    return { state: 'stop', reason: `ledger schrijven mislukt (${err?.status || 'fout'})` };
  }
  return { state: 'ok' };
}

/**
 * Eerste stap van de agentjob (op code van de default branch): staat er een geverifieerde ledgerentry
 * voor de HUIDIGE poging, en is die de nieuwste? "Re-run failed jobs" hergebruikt de uitvoer van de
 * oude poort; zonder entry voor deze poging start er dus geen agent. Daarnaast: issue open en
 * exclusief `turn: claude-code`. Geeft `{ ok, reason }`; een API-fout propageert.
 */
async function checkAgentStart({ github, context, issueNumber, mode, runId, runAttempt, defaultBranch = '' }) {
  const cfg = MODES[mode];
  const no = (reason) => ({ ok: false, reason });
  const comments = await listIssueComments({ github, context, issueNumber });
  const found = comments.filter((c) => isLedgerComment(c, cfg.marker));
  if (found.length !== 1) return no(`${found.length} ledgers gevonden (precies één verwacht)`);
  let ledger;
  try {
    ledger = parseLedger(found[0].body, cfg.marker);
  } catch (err) {
    if (err instanceof LedgerError) return no(`ledger onleesbaar: ${err.message}`);
    throw err;
  }
  const mine = ledger.entries.find((e) => e.run === runId && e.run_attempt === runAttempt);
  if (!mine) return no(`geen ledgerentry voor run ${runId} poging ${runAttempt}`);
  const newest = ledger.entries.reduce((a, b) => (iso(b.created_at) >= iso(a.created_at) ? b : a));
  if (newest !== mine) return no('een nieuwere beurt staat in de ledger (verouderde run)');

  const own = await loadOwnRun({ github, context, runId, runAttempt });
  const v = await verifyEntry({ github, context, entry: mine, issueNumber, mode, own, defaultBranch });
  if (!v.ok) return no(v.reason);

  const issue = (await github.rest.issues.get({
    owner: context.repo.owner, repo: context.repo.repo, issue_number: issueNumber, headers: API_VERSION_HEADERS,
  })).data;
  if (issue.state !== 'open') return no(`het issue is niet meer open (state: ${issue.state})`);
  const turn = (issue.labels || []).map((l) => (typeof l === 'string' ? l : l.name)).filter((n) => n.startsWith('turn: '));
  if (turn.length !== 1 || turn[0] !== 'turn: claude-code') {
    return no(`de actuele beurt is niet exclusief \`turn: claude-code\` (nu: ${turn.join(', ') || 'geen'})`);
  }
  return { ok: true };
}

module.exports = {
  MAX_RUNS, MODES, GATE_JOB_NAMES, LedgerError,
  annotationText, parseLedger, serializeLedger, isLedgerComment, isOwnerReset, hasTraces,
  verifyEntry, evaluateLedger, recordEntry, markEnded, checkAgentStart,
};
