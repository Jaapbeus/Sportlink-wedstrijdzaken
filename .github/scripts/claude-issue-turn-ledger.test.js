// Unit-tests voor claude-issue-turn-ledger.js — zonder GitHub, zonder token, zonder netwerk.
// Uitvoeren: node .github/scripts/claude-issue-turn-ledger.test.js   (exit 0 = alles groen)
// Draait bij elke PR in de job 'Build FunctionApp + BlazorAdmin' (build.yml). De ledger draait in fase 1 alleen
// in de testmodus van de issue-router; zonder deze test blijkt een fout pas bij een echte proef (#1721).

const L = require('./claude-issue-turn-ledger.js');

let failures = 0;
function check(name, actual, expected) {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a === e) { console.log(`  PASS  ${name}`); }
  else { console.log(`  FAIL  ${name}\n        verwacht: ${e}\n        kreeg:    ${a}`); failures++; }
}
async function rejects(name, fn, status) {
  try { await fn(); console.log(`  FAIL  ${name}\n        verwacht een fout, kreeg geen`); failures++; }
  catch (err) { check(name, err.status, status); }
}

const GATE = L.GATE_JOB_NAMES[1];
const ctx = { repo: { owner: 'o', repo: 'r' } };
const ISSUE = 42;
const WF = 77;
const PATH = '.github/workflows/issue-router.yml';
const T = L.MODES.test.marker;
const P = L.MODES.prod.marker;
const botUser = { type: 'Bot', login: 'github-actions[bot]' };

function apiError(status) { const e = new Error(`HTTP ${status}`); e.status = status; return e; }

/** Een volledig consistente poging (run, jobs en annotatie) voor het issue; `mut` mag er iets aan breken. */
function addAttempt(env, run, attempt, { issue = ISSUE, event = 'workflow_dispatch', created = '2026-10-14T09:00:00Z', mut = {} } = {}) {
  const key = `${run}#${attempt}`;
  env.runs[key] = {
    id: run, run_attempt: attempt, workflow_id: WF, path: `${PATH}@refs/heads/feature/x`, event,
    head_branch: 'feature/x', head_sha: 'abc', created_at: created, status: 'completed',
    triggering_actor: { login: 'alice', type: 'User' }, actor: { login: 'alice', type: 'User' },
    ...(mut.run || {}),
  };
  const jobId = run * 10 + attempt;
  env.jobs[key] = [{ id: jobId, name: GATE, conclusion: 'success', head_sha: 'abc', run_attempt: attempt, ...(mut.job || {}) }]
    .concat(mut.extraJobs || []);
  env.annotations[jobId] = mut.annotations
    || [{ message: L.annotationText({ issueNumber: issue, runId: run, runAttempt: attempt }) }];
  if (mut.noRun) delete env.runs[key];
}

function makeEnv(extra = {}) {
  const env = {
    comments: [], runs: {}, jobs: {}, annotations: {}, perms: { alice: 'write', bob: 'read' }, nextId: 1000,
    issue: { state: 'open', labels: [{ name: 'turn: claude-code' }] }, failWrite: false, failRun: null, ...extra,
  };
  addAttempt(env, 500, 1, { created: '2026-10-14T12:00:00Z' }); // de huidige run
  env.github = {
    paginate: async (fn, params) => (await fn(params)).data,
    rest: {
      issues: {
        listComments: async () => ({ data: env.comments }),
        createComment: async ({ body }) => {
          if (env.failWrite) throw apiError(500);
          const c = { id: env.nextId++, body, user: botUser, created_at: '2026-10-14T12:00:01Z', updated_at: '2026-10-14T12:00:01Z' };
          env.comments.push(c); return { data: c };
        },
        updateComment: async ({ comment_id, body }) => {
          if (env.failWrite) throw apiError(500);
          const c = env.comments.find((x) => x.id === comment_id); c.body = body; c.updated_at = '2026-10-14T12:00:02Z';
          return { data: c };
        },
        get: async () => ({ data: env.issue }),
      },
      actions: {
        getWorkflowRunAttempt: async ({ run_id, attempt_number }) => {
          if (env.failRun === run_id) throw apiError(503);
          const r = env.runs[`${run_id}#${attempt_number}`];
          if (!r) throw apiError(404);
          return { data: r };
        },
        listJobsForWorkflowRunAttempt: async ({ run_id, attempt_number }) => ({ data: env.jobs[`${run_id}#${attempt_number}`] || [] }),
      },
      checks: { listAnnotations: async ({ check_run_id }) => ({ data: env.annotations[check_run_id] || [] }) },
      repos: { getCollaboratorPermissionLevel: async ({ username }) => ({ data: { permission: env.perms[username] || 'none' } }) },
    },
  };
  return env;
}

function ledgerComment(marker, entries, { id = 900, reset = null, updated = '2026-10-14T10:00:00Z' } = {}) {
  return { id, body: L.serializeLedger({ entries, reset_comment_id: reset }, marker), user: botUser, created_at: updated, updated_at: updated };
}
const entry = (run, attempt = 1, ended = null) => ({ run, run_attempt: attempt, created_at: '2026-10-14T09:00:00Z', ended_at: ended });
const ownerReset = (id, at) => ({
  id, body: 'Ledger-reset: bewuste reset', author_association: 'OWNER', user: { type: 'User', login: 'owner' }, created_at: at,
});
const evalTest = (env, extra = {}) => L.evaluateLedger({
  github: env.github, context: ctx, issueNumber: ISSUE, mode: 'test', runId: 500, runAttempt: 1, ...extra,
});
const evalProd = (env, extra = {}) => L.evaluateLedger({
  github: env.github, context: ctx, issueNumber: ISSUE, mode: 'prod', runId: 500, runAttempt: 1, defaultBranch: 'main', ...extra,
});

(async () => {
  console.log('parseLedger / serializeLedger:');
  {
    const ledger = { entries: [entry(1), entry(2, 2, '2026-10-14T09:30:00Z')], reset_comment_id: 7 };
    check('rondreis', L.parseLedger(L.serializeLedger(ledger, T), T), ledger);
    const bad = (body, naam) => {
      let msg = null;
      try { L.parseLedger(body, T); } catch (e) { msg = e instanceof L.LedgerError ? 'LedgerError' : String(e); }
      check(naam, msg, 'LedgerError');
    };
    bad('geen marker', 'zonder marker');
    bad(`${P}\n\`\`\`json\n{"entries":[],"reset_comment_id":null}\n\`\`\``, 'marker van productie telt niet voor de testledger');
    bad(`${T}\nvrije tekst`, 'geen JSON-blok');
    bad(`${T}\n\`\`\`json\n{kapot\n\`\`\``, 'JSON onleesbaar');
    bad(`${T}\n\`\`\`json\n{"entries":{},"reset_comment_id":null}\n\`\`\``, 'entries geen lijst');
    bad(`${T}\n\`\`\`json\n{"entries":[],"reset_comment_id":"x"}\n\`\`\``, 'reset_comment_id ongeldig');
    bad(L.serializeLedger({ entries: [entry(1), entry(1)], reset_comment_id: null }, T), 'dubbele sleutel (run, poging)');
    bad(L.serializeLedger({ entries: [{ run: '1', run_attempt: 1, created_at: 'x', ended_at: null }], reset_comment_id: null }, T), 'ongeldige entry');
    check('zelfde run, andere poging is geen duplicaat', L.parseLedger(L.serializeLedger({ entries: [entry(1, 1), entry(1, 2)], reset_comment_id: null }, T), T).entries.length, 2);
  }

  console.log('\nledgercomment en reset herkennen:');
  {
    check('bot met marker', L.isLedgerComment(ledgerComment(T, []), T), true);
    check('mens met dezelfde marker telt niet', L.isLedgerComment({ ...ledgerComment(T, []), user: { type: 'User', login: 'x' } }, T), false);
    check('andere bot met dezelfde marker telt niet', L.isLedgerComment({ ...ledgerComment(T, []), user: { type: 'Bot', login: 'claude[bot]' } }, T), false);
    check('owner-reset herkend', L.isOwnerReset(ownerReset(1, 'x')), true);
    check('reset van een niet-eigenaar telt niet', L.isOwnerReset({ ...ownerReset(1, 'x'), author_association: 'COLLABORATOR' }), false);
    check('reset zonder reden telt niet', L.isOwnerReset({ ...ownerReset(1, 'x'), body: 'Ledger-reset:' }), false);
  }

  console.log('\nevaluateLedger (testmodus):');
  {
    const env = makeEnv();
    const r = await evalTest(env);
    check('geen ledger: ok, prior 0', [r.state, r.prior], ['ok', 0]);
    const w = await L.recordEntry({ github: env.github, context: ctx, issueNumber: ISSUE, mode: 'test', evaluated: r, runId: 500, runAttempt: 1, now: () => '2026-10-14T12:00:00Z' });
    check('entry geschreven', w.state, 'ok');
    const stored = L.parseLedger(env.comments[0].body, T);
    check('ledger bevat de eigen sleutel', stored.entries, [{ run: 500, run_attempt: 1, created_at: '2026-10-14T12:00:00Z', ended_at: null }]);
    const again = await evalTest(env);
    await L.recordEntry({ github: env.github, context: ctx, issueNumber: ISSUE, mode: 'test', evaluated: again, runId: 500, runAttempt: 1 });
    check('dubbel schrijven is idempotent (geen tweede entry)', L.parseLedger(env.comments[0].body, T).entries.length, 1);
    check('eigen entry telt niet als eerdere beurt', again.prior, 0);
    check('slechts één ledgercomment', env.comments.length, 1);
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(11), entry(12)]));
    addAttempt(env, 11, 1); addAttempt(env, 12, 1);
    const r = await evalTest(env);
    check('twee geverifieerde entries: ok, prior 2', [r.state, r.prior], ['ok', 2]);
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(11), entry(12), entry(13)]));
    [11, 12, 13].forEach((n) => addAttempt(env, n, 1));
    const r = await evalTest(env);
    check(`drie entries: stop bij limiet (${L.MAX_RUNS})`, [r.state, /drempel 3/.test(r.reason)], ['stop', true]);
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, []), { ...ledgerComment(T, []), id: 901 });
    check('twee ledgers: stop', (await evalTest(env)).state, 'stop');
  }
  {
    const env = makeEnv();
    env.comments.push({ ...ledgerComment(T, []), body: `${T}\nkapot` });
    const r = await evalTest(env);
    check('parsefout: stop', [r.state, /onleesbaar/.test(r.reason)], ['stop', true]);
  }
  {
    const env = makeEnv();
    env.comments.push({ ...ledgerComment(T, []), user: { type: 'User', login: 'mallory' }, body: `${T}\nbeïnvloeden` });
    const r = await evalTest(env);
    check('comment van een mens met de marker wordt genegeerd (geen stop, geen tweede ledger)', r.state, 'ok');
  }

  console.log('\nbewijs per entry (elke afwijking stopt, fail-closed):');
  const cases = [
    ['run bestaat niet (verwijderd)', { noRun: true }],
    ['andere workflow', { run: { workflow_id: 5 } }],
    ['ander workflowpad', { run: { path: '.github/workflows/anders.yml@refs/heads/x' } }],
    ['ander event (issues in testmodus)', { run: { event: 'issues' } }],
    ['actor zonder schrijfrecht', { run: { actor: { login: 'bob', type: 'User' } } }],
    ['herstarter zonder schrijfrecht (oorspronkelijke actor wel)', { run: { triggering_actor: { login: 'bob', type: 'User' } } }],
    ['actor is een bot', { run: { triggering_actor: { login: 'github-actions[bot]', type: 'Bot' } } }],
    ['actor ontbreekt', { run: { triggering_actor: null } }],
    ['poortjob niet geslaagd', { job: { conclusion: 'failure' } }],
    ['poortjob van een andere head-SHA', { job: { head_sha: 'zzz' } }],
    ['poortjob van een andere poging', { job: { run_attempt: 2 } }],
    ['twee poortjobs', { extraJobs: [{ id: 9, name: GATE, conclusion: 'success', head_sha: 'abc', run_attempt: 1 }] }],
    ['geen annotatie', { annotations: [] }],
    ['annotatie van een ander issue', { annotations: [{ message: L.annotationText({ issueNumber: 999, runId: 11, runAttempt: 1 }) }] }],
    ['annotatie van een andere run', { annotations: [{ message: L.annotationText({ issueNumber: ISSUE, runId: 12, runAttempt: 1 }) }] }],
    ['annotatie van een andere poging', { annotations: [{ message: L.annotationText({ issueNumber: ISSUE, runId: 11, runAttempt: 2 }) }] }],
    ['twee beurtannotaties', { annotations: [{ message: L.annotationText({ issueNumber: ISSUE, runId: 11, runAttempt: 1 }) }, { message: L.annotationText({ issueNumber: ISSUE, runId: 11, runAttempt: 1 }) }] }],
    ['annotatie met extra tekst', { annotations: [{ message: `${L.annotationText({ issueNumber: ISSUE, runId: 11, runAttempt: 1 })} extra` }] }],
  ];
  for (const [naam, mut] of cases) {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(11)]));
    addAttempt(env, 11, 1, { mut });
    const r = await evalTest(env);
    check(`${naam}: stop`, [r.state, /niet te verifiëren/.test(r.reason || '')], ['stop', true]);
  }
  {
    // Gemengde paren: de ledger zegt poging 2, het bewijs bestaat alleen voor poging 1.
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(11, 2)]));
    addAttempt(env, 11, 1);
    check('entry voor poging 2 zonder bewijs voor poging 2: stop', (await evalTest(env)).state, 'stop');
  }
  {
    const env = makeEnv({ failRun: 11 });
    env.comments.push(ledgerComment(T, [entry(11)]));
    addAttempt(env, 11, 1);
    await rejects('API-fout (503) propageert: job rood, geen stille stop of telling', () => evalTest(env), 503);
  }

  console.log('\nveroudering en schrijffout:');
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(11, 1, '2026-10-14T13:00:00Z')]));
    addAttempt(env, 11, 1);
    const r = await evalTest(env);
    check('run aangemaakt vóór het einde van de laatste beurt: stop (verouderd)', [r.state, /verouderde run/.test(r.reason)], ['stop', true]);
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(11, 1, '2026-10-14T11:00:00Z')]));
    addAttempt(env, 11, 1);
    check('nieuwere run dan de laatste beurt: ok', (await evalTest(env)).state, 'ok');
  }
  {
    const env = makeEnv();
    const r = await evalTest(env);
    env.failWrite = true;
    const w = await L.recordEntry({ github: env.github, context: ctx, issueNumber: ISSUE, mode: 'test', evaluated: r, runId: 500, runAttempt: 1 });
    check('mislukte write: stop', [w.state, /schrijven mislukt/.test(w.reason)], ['stop', true]);
  }

  console.log('\nproductiemodus (sporen, default branch, reset):');
  {
    const env = makeEnv();
    env.runs['500#1'].head_branch = 'main';
    env.comments.push({ id: 1, body: 'eerdere beurt', user: { type: 'Bot', login: 'claude[bot]' }, created_at: '2026-10-14T08:00:00Z' });
    const r = await evalProd(env);
    check('geen ledger maar sporen van een eerdere beurt: stop', [r.state, /sporen/.test(r.reason)], ['stop', true]);
  }
  {
    const env = makeEnv();
    env.comments.push({ id: 2, body: 'Fase: review\nPR: #1', user: botUser, created_at: '2026-10-14T08:00:00Z' });
    check('automatische Fase-comment telt als spoor: stop', (await evalProd(env)).state, 'stop');
  }
  {
    const env = makeEnv();
    env.comments.push({ id: 3, body: 'gewoon commentaar', user: { type: 'User', login: 'x' }, created_at: '2026-10-14T08:00:00Z' });
    const r = await evalProd(env);
    check('geen ledger en geen sporen (nieuw issue): ok', [r.state, r.prior], ['ok', 0]);
  }
  {
    const env = makeEnv();
    env.comments.push({ id: 1, body: 'eerdere beurt', user: { type: 'Bot', login: 'claude[bot]' }, created_at: '2026-10-14T08:00:00Z' }, ownerReset(50, '2026-10-14T09:00:00Z'));
    const r = await evalProd(env);
    check('sporen + eigenaarsreset: ok, reset_comment_id vastgelegd', [r.state, r.ledger?.reset_comment_id], ['ok', 50]);
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(P, [entry(11)]));
    addAttempt(env, 11, 1, { event: 'issues' });
    env.runs['11#1'].head_branch = 'develop';
    const r = await evalProd(env);
    check('productie: run niet op de default branch: stop', [r.state, /niet te verifiëren/.test(r.reason)], ['stop', true]);
    env.runs['11#1'].head_branch = 'main';
    check('productie: event issues op de default branch: ok', (await evalProd(env)).state, 'ok');
  }
  {
    const full = [entry(11), entry(12), entry(13)];
    const env = makeEnv();
    env.comments.push(ledgerComment(T, full), ownerReset(60, '2026-10-14T11:00:00Z'));
    full.forEach((e) => addAttempt(env, e.run, 1));
    const r = await evalTest(env);
    check('reset nieuwer dan de ledger: entries leeg, prior 0', [r.state, r.prior, r.ledger?.reset_comment_id], ['ok', 0, 60]);
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(11), entry(12), entry(13)]), ownerReset(60, '2026-10-14T09:00:00Z'));
    [11, 12, 13].forEach((n) => addAttempt(env, n, 1));
    check('reset ouder dan de laatste ledgerupdate telt niet: stop op limiet', (await evalTest(env)).state, 'stop');
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(11), entry(12), entry(13)], { reset: 60 }), ownerReset(60, '2026-10-14T11:00:00Z'));
    [11, 12, 13].forEach((n) => addAttempt(env, n, 1));
    check('een reeds gebruikte reset wordt niet hergebruikt', (await evalTest(env)).state, 'stop');
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(11), entry(12), entry(13)]), { ...ownerReset(61, '2026-10-14T11:00:00Z'), author_association: 'COLLABORATOR' });
    [11, 12, 13].forEach((n) => addAttempt(env, n, 1));
    check('reset van een niet-eigenaar telt niet', (await evalTest(env)).state, 'stop');
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(11)]), ownerReset(62, '2026-10-14T11:00:00Z'));
    addAttempt(env, 11, 1);
    env.runs['11#1'].status = 'in_progress';
    const r = await evalTest(env);
    check('reset geweigerd terwijl een beurt loopt', [r.state, /reset geweigerd/.test(r.reason)], ['stop', true]);
  }

  console.log('\nmarkEnded:');
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(500, 1)]));
    const r = await L.markEnded({ github: env.github, context: ctx, issueNumber: ISSUE, mode: 'test', runId: 500, runAttempt: 1, now: () => '2026-10-14T12:30:00Z' });
    check('ended_at ingevuld', [r.state, L.parseLedger(env.comments[0].body, T).entries[0].ended_at], ['ok', '2026-10-14T12:30:00Z']);
    const again = await L.markEnded({ github: env.github, context: ctx, issueNumber: ISSUE, mode: 'test', runId: 500, runAttempt: 1, now: () => '2099-01-01T00:00:00Z' });
    check('tweede keer: ongewijzigd', [again.state, again.unchanged, L.parseLedger(env.comments[0].body, T).entries[0].ended_at], ['ok', true, '2026-10-14T12:30:00Z']);
    const missing = await L.markEnded({ github: env.github, context: ctx, issueNumber: ISSUE, mode: 'test', runId: 501, runAttempt: 1 });
    check('eigen entry ontbreekt: stop', missing.state, 'stop');
  }

  console.log('\ncheckAgentStart (eerste stap van de agentjob):');
  const start = (env, extra = {}) => L.checkAgentStart({ github: env.github, context: ctx, issueNumber: ISSUE, mode: 'test', runId: 500, runAttempt: 1, ...extra });
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [entry(11), { run: 500, run_attempt: 1, created_at: '2026-10-14T12:00:00Z', ended_at: null }]));
    addAttempt(env, 11, 1);
    check('geldige entry voor de huidige poging: ok', (await start(env)).ok, true);
    env.issue = { state: 'closed', labels: [{ name: 'turn: claude-code' }] };
    const closed = await start(env);
    check('issue gesloten: niet ok', [closed.ok, /niet meer open/.test(closed.reason)], [false, true]);
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [{ run: 500, run_attempt: 1, created_at: '2026-10-14T12:00:00Z', ended_at: null }]));
    const r = await start(env, { runAttempt: 2 });
    check('"Re-run failed jobs": geen entry voor poging 2 → geen agent', [r.ok, /poging 2/.test(r.reason)], [false, true]);
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [{ run: 500, run_attempt: 1, created_at: '2026-10-14T12:00:00Z', ended_at: null }, { run: 501, run_attempt: 1, created_at: '2026-10-14T12:05:00Z', ended_at: null }]));
    const r = await start(env);
    check('een nieuwere beurt staat in de ledger: niet ok', [r.ok, /nieuwere beurt/.test(r.reason)], [false, true]);
  }
  {
    const env = makeEnv();
    check('geen ledger: niet ok', (await start(env)).ok, false);
  }
  {
    const env = makeEnv({ issue: { state: 'open', labels: [{ name: 'turn: claude-code' }, { name: 'turn: owner' }] } });
    env.comments.push(ledgerComment(T, [{ run: 500, run_attempt: 1, created_at: '2026-10-14T12:00:00Z', ended_at: null }]));
    const r = await start(env);
    check('turn-label niet exclusief: niet ok', [r.ok, /exclusief/.test(r.reason)], [false, true]);
  }
  {
    const env = makeEnv();
    env.comments.push(ledgerComment(T, [{ run: 500, run_attempt: 1, created_at: '2026-10-14T12:00:00Z', ended_at: null }]));
    env.annotations[5001] = [];
    const r = await start(env);
    check('eigen poortjob zonder annotatie: niet ok', r.ok, false);
  }

  console.log(failures === 0 ? '\nAlle checks geslaagd.' : `\n${failures} check(s) gefaald.`);
  process.exit(failures === 0 ? 0 : 1);
})();
