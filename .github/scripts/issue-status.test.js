// Unit-tests voor issue-status.js — draaien zonder GitHub, zonder token, zonder netwerk.
// Uitvoeren: node .github/scripts/issue-status.test.js   (exit 0 = alles groen)
// Wordt bij elke PR gedraaid door de job 'Build FunctionApp + BlazorAdmin' in build.yml.
const { extractIssueRefs, setIssueStatus, clearIssueStatus, clearTurnLabels, isEpic, updatePullRequestIssueStatuses,
  statusVoorPullRequest, plaatsFasecommentGemerged, reconcileIssueStatus, FASE_GEMERGED_MARKER } = require('./issue-status.js');

let failures = 0;
function check(name, actual, expected) {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a === e) { console.log(`  PASS  ${name}`); }
  else { console.log(`  FAIL  ${name}\n        verwacht: ${e}\n        kreeg:    ${a}`); failures++; }
}

// ---------- extractIssueRefs ----------
console.log('extractIssueRefs:');
let r = extractIssueRefs('fix(#684): iets', 'Lost op. Zie ook #683 en #123.');
check('titel-nummer is strong', [...r.strong], [684]);
check('proza-verwijzing alleen in all', r.all.sort((a,b)=>a-b), [123, 683, 684]);

// #1179 — titel-attributie is gebonden aan HAAKJES. Elk nummer in de titel meenemen
// ondermijnde de #838/#630-versmalling in de workflow die issues sluit. De gevallen hieronder
// zijn letterlijke PR-titels uit deze repo.
r = extractIssueRefs('refactor(#1122): review epic #986 — endpoint-helper, code-behind', '');
check('epic-kruisverwijzing in titelproza is NIET strong', [...r.strong], [1122]);
check('die kruisverwijzing staat wel in all', r.all.sort((a,b)=>a-b), [986, 1122]);

r = extractIssueRefs('docs(#1051): CHANGELOG-verwijzing naar #1048 gebruikte haakjesnotatie', '');
check('onderwerp-van-de-zin is NIET strong', [...r.strong], [1051]);

r = extractIssueRefs('fix(#1131): import atomisch + club-lock (Postgres, #1132)', '');
check('tweede haakjesgroep met tekst telt wel mee', [...r.strong].sort((a,b)=>a-b), [1131, 1132]);

r = extractIssueRefs('feat(#1093): migraties vóór de code + fix(#1112): checksum', '');
check('twee voorvoegsels in één titel', [...r.strong].sort((a,b)=>a-b), [1093, 1112]);

r = extractIssueRefs('chore: sync develop met main-hotfixes (#1095, #1098, #1099, #1101)', '');
check('titel zonder voorvoegsel, wel een haakjesgroep', [...r.strong].sort((a,b)=>a-b), [1095, 1098, 1099, 1101]);

r = extractIssueRefs('chore: main terugmergen — productiefixes #972 en #976 ontbraken', '');
check('kale nummers zonder haakjes leveren niets op', [...r.strong], []);

r = extractIssueRefs('chore: geen nummer', 'Closes #42\nFixes: #43\nresolved #44\nzie #45');
check('sluitende keywords zijn strong', [...r.strong].sort((a,b)=>a-b), [42, 43, 44]);
check('#45 niet strong', r.strong.has(45), false);

r = extractIssueRefs('', '');
check('leeg levert niets', { all: r.all, strong: [...r.strong] }, { all: [], strong: [] });

r = extractIssueRefs(null, null);
check('null is veilig', { all: r.all, strong: [...r.strong] }, { all: [], strong: [] });

// ---------- isEpic ----------
console.log('\nisEpic:');
check('object-labels: epic aanwezig', isEpic({ labels: [{ name: 'epic' }, { name: 'type: ci' }] }), true);
check('string-labels: epic aanwezig', isEpic({ labels: ['epic', 'priority: low'] }), true);
check('geen epic-label', isEpic({ labels: [{ name: 'type: ci' }] }), false);
check('geen labels', isEpic({ labels: [] }), false);
check('labels ontbreekt volledig', isEpic({}), false);

// ---------- setIssueStatus ----------
console.log('\nsetIssueStatus:');

function fakeGithub(labels, opts = {}) {
  const calls = { added: [], removed: [] };
  return {
    calls,
    rest: {
      issues: {
        get: async () => {
          if (opts.throws) throw new Error('404');
          return { data: { labels: labels.map(name => ({ name })), pull_request: opts.isPr ? {} : undefined } };
        },
        addLabels: async ({ labels: l }) => { calls.added.push(...l); },
        removeLabel: async ({ name }) => { calls.removed.push(name); },
      },
    },
  };
}
const ctx = { repo: { owner: 'o', repo: 'r' } };
const core = { notice: () => {}, warning: () => {} };

async function run() {
  let gh = fakeGithub(['type: bug']);
  let res = await setIssueStatus({ github: gh, context: ctx, core, issueNumber: 1, status: 'status: review-needed' });
  check('geen status → toevoegen', { res, added: gh.calls.added, removed: gh.calls.removed },
        { res: 'set', added: ['status: review-needed'], removed: [] });

  gh = fakeGithub(['status: in-progress', 'type: bug']);
  res = await setIssueStatus({ github: gh, context: ctx, core, issueNumber: 1, status: 'status: review-needed' });
  check('oude status wordt vervangen', { res, added: gh.calls.added, removed: gh.calls.removed },
        { res: 'set', added: ['status: review-needed'], removed: ['status: in-progress'] });

  gh = fakeGithub(['status: review-needed']);
  res = await setIssueStatus({ github: gh, context: ctx, core, issueNumber: 1, status: 'status: review-needed' });
  check('idempotent', { res, added: gh.calls.added, removed: gh.calls.removed },
        { res: 'unchanged', added: [], removed: [] });

  gh = fakeGithub(['status: blocked']);
  res = await setIssueStatus({ github: gh, context: ctx, core, issueNumber: 1, status: 'status: review-needed' });
  check('blocked niet overschreven', { res, added: gh.calls.added }, { res: 'protected', added: [] });

  gh = fakeGithub(['status: waiting-codex']);
  res = await setIssueStatus({ github: gh, context: ctx, core, issueNumber: 1, status: 'status: review-needed' });
  check('waiting-codex niet overschreven door automatisering (#1336)', { res, added: gh.calls.added },
        { res: 'protected', added: [] });

  gh = fakeGithub(['status: awaiting-release']);
  res = await setIssueStatus({ github: gh, context: ctx, core, issueNumber: 1, status: 'status: review-needed' });
  check('awaiting-release blijft staan bij PR-status', { res, added: gh.calls.added, removed: gh.calls.removed },
        { res: 'protected', added: [], removed: [] });

  gh = fakeGithub(['status: waiting-owner']);
  res = await setIssueStatus({ github: gh, context: ctx, core, issueNumber: 1, status: 'status: awaiting-release', respectProtected: false });
  check('merge overschrijft waiting-owner', { res, added: gh.calls.added, removed: gh.calls.removed },
        { res: 'set', added: ['status: awaiting-release'], removed: ['status: waiting-owner'] });

  gh = fakeGithub(['status: awaiting-release']);
  res = await setIssueStatus({ github: gh, context: ctx, core, issueNumber: 1, status: 'status: triage', onlyIfNone: true });
  check('onlyIfNone laat bestaande staan', { res, added: gh.calls.added }, { res: 'unchanged', added: [] });

  gh = fakeGithub([]);
  res = await setIssueStatus({ github: gh, context: ctx, core, issueNumber: 1, status: 'status: triage', onlyIfNone: true });
  check('onlyIfNone zet bij leeg', { res, added: gh.calls.added }, { res: 'set', added: ['status: triage'] });

  gh = fakeGithub(['status: in-progress'], { isPr: true });
  res = await setIssueStatus({ github: gh, context: ctx, core, issueNumber: 1, status: 'status: triage' });
  check('PRs overgeslagen', { res, added: gh.calls.added }, { res: 'skipped', added: [] });

  gh = fakeGithub([], { throws: true });
  res = await setIssueStatus({ github: gh, context: ctx, core, issueNumber: 1, status: 'status: triage' });
  check('ophaalfout is niet fataal', res, 'skipped');

  // The workflow calls this function directly. A forged strong reference must never reach
  // issues.addLabels unless both PR origin and the author's current write access are proven.
  console.log('\nPR status provenance:');
  function prEvent(overrides = {}) {
    const pr = {
      title: 'fix(#42): synthetic change', body: 'Related to #43', draft: true, merged: false,
      user: { login: 'writer' },
      head: { repo: { id: 7 } }, base: { repo: { id: 7 } },
      ...overrides,
    };
    return { repo: ctx.repo, payload: { repository: { id: 7 }, action: 'opened', pull_request: pr } };
  }
  function prGithub(labels, permission = 'write') {
    const client = fakeGithub(labels);
    client.calls.permissionChecks = 0;
    client.request = async () => {
      client.calls.permissionChecks++;
      if (permission === 'error') throw new Error('private API detail');
      return { data: { permission } };
    };
    return client;
  }
  async function runPrCase(name, context, permission, expected) {
    const client = prGithub(['source: codex', 'status: triage'], permission);
    const outcome = await updatePullRequestIssueStatuses({ github: client, context, core });
    check(name, { outcome, added: client.calls.added, removed: client.calls.removed,
      permissionChecks: client.calls.permissionChecks }, expected);
  }

  await runPrCase('trusted branch and writer follows strong reference', prEvent(), 'write',
    { outcome: 'updated', added: ['status: in-progress'], removed: ['status: triage'], permissionChecks: 1 });
  const readyContext = prEvent({ draft: false });
  readyContext.payload.action = 'ready_for_review';
  await runPrCase('trusted ready PR enters review', readyContext, 'admin',
    { outcome: 'updated', added: ['status: review-needed'], removed: ['status: triage'], permissionChecks: 1 });
  const draftContext = prEvent({ draft: true });
  draftContext.payload.action = 'converted_to_draft';
  await runPrCase('trusted draft conversion resumes work', draftContext, 'write',
    { outcome: 'updated', added: ['status: in-progress'], removed: ['status: triage'], permissionChecks: 1 });
  const closedContext = prEvent();
  closedContext.payload.action = 'closed';
  await runPrCase('trusted unmerged closure returns to triage', closedContext, 'write',
    { outcome: 'updated', added: [], removed: [], permissionChecks: 1 });
  await runPrCase('fork cannot relabel despite strong reference', prEvent({ head: { repo: { id: 8 } } }), 'write',
    { outcome: 'untrusted', added: [], removed: [], permissionChecks: 0 });
  await runPrCase('read-only author cannot relabel own-repo PR', prEvent(), 'read',
    { outcome: 'untrusted', added: [], removed: [], permissionChecks: 1 });
  await runPrCase('unknown permission cannot relabel', prEvent(), null,
    { outcome: 'untrusted', added: [], removed: [], permissionChecks: 1 });
  await runPrCase('permission API error cannot relabel', prEvent(), 'error',
    { outcome: 'untrusted', added: [], removed: [], permissionChecks: 1 });
  await runPrCase('Dependabot uit dezelfde repository heeft geen permissie-aanroep nodig',
    prEvent({ user: { login: 'dependabot[bot]', type: 'Bot' } }), 'error',
    { outcome: 'updated', added: ['status: in-progress'], removed: ['status: triage'], permissionChecks: 0 });
  await runPrCase('Dependabot uit een fork blijft geweigerd',
    prEvent({ user: { login: 'dependabot[bot]', type: 'Bot' }, head: { repo: { id: 8 } } }), 'error',
    { outcome: 'untrusted', added: [], removed: [], permissionChecks: 0 });
  await runPrCase('Een andere bot krijgt geen vrijstelling',
    prEvent({ user: { login: 'andere-app[bot]', type: 'Bot' } }), 'error',
    { outcome: 'untrusted', added: [], removed: [], permissionChecks: 1 });
  await runPrCase('Een gebruikersnaam zonder Bot-type krijgt geen vrijstelling',
    prEvent({ user: { login: 'dependabot[bot]', type: 'User' } }), 'error',
    { outcome: 'untrusted', added: [], removed: [], permissionChecks: 1 });
  const warnings = [];
  gh = prGithub(['status: triage'], 'error');
  await updatePullRequestIssueStatuses({ github: gh, context: prEvent(),
    core: { notice: () => {}, warning: message => warnings.push(message) } });
  check('mislukte permissie-aanroep waarschuwt concreet', warnings.length === 1 && warnings[0].includes('workflowtoken') && warnings[0].includes('opnieuw'), true);
  check('permission error does not disclose API detail', warnings.some(message => message.includes('private API detail')), false);
  await runPrCase('missing origin cannot relabel', prEvent({ head: {} }), 'write',
    { outcome: 'untrusted', added: [], removed: [], permissionChecks: 0 });

  gh = prGithub(['source: owner', 'status: awaiting-release']);
  res = await updatePullRequestIssueStatuses({ github: gh, context: prEvent({ draft: false }), core });
  check('trusted PR cannot move awaiting-release or source',
    { res, added: gh.calls.added, removed: gh.calls.removed },
    { res: 'updated', added: [], removed: [] });

  gh = prGithub(['source: owner', 'status: review-needed']);
  const mergedContext = prEvent({ merged: true });
  mergedContext.payload.action = 'closed';
  res = await updatePullRequestIssueStatuses({ github: gh, context: mergedContext, core });
  check('develop merge leaves status to release workflow',
    { res, added: gh.calls.added, removed: gh.calls.removed, permissionChecks: gh.calls.permissionChecks },
    { res: 'merged', added: [], removed: [], permissionChecks: 0 });

  gh = fakeGithub(['status: awaiting-release', 'status: in-progress', 'type: bug']);
  res = await clearIssueStatus({ github: gh, context: ctx, core, issueNumber: 1 });
  check('clear verwijdert alle statussen', { res, added: gh.calls.added, removed: gh.calls.removed.sort() },
        { res: 'set', added: [], removed: ['status: awaiting-release', 'status: in-progress'] });

  // ---------- label-awaiting-release.yml: epic-guard (#838) ----------
  // Simuleert de per-issue-beslissing uit de labelloop van label-awaiting-release.yml:
  // een issue dat in de PR-body wordt genoemd, krijgt 'status: awaiting-release' —
  // BEHALVE als het het label 'epic' draagt. Epics worden nooit via een
  // 'fix(#NNN):'-commit-subject of CHANGELOG-'(#NNN)'-attributie afgehandeld, dus
  // close-released-issues.yml verwijdert dat label bij een epic nooit meer.
  console.log('\nlabel-awaiting-release.yml epic-guard:');

  async function simulateAwaitingReleaseGuard(github) {
    const issue = (await github.rest.issues.get({ owner: 'o', repo: 'r', issue_number: 1 })).data;
    if (issue.pull_request) return 'skipped-pr';
    if (isEpic(issue)) return 'skipped-epic';
    return setIssueStatus({ github, context: ctx, core, issueNumber: 1, status: 'status: awaiting-release', respectProtected: false });
  }

  gh = fakeGithub(['epic', 'type: ci']);
  res = await simulateAwaitingReleaseGuard(gh);
  check('epic genoemd in PR-body krijgt GEEN awaiting-release', { res, added: gh.calls.added },
        { res: 'skipped-epic', added: [] });

  gh = fakeGithub(['type: ci']);
  res = await simulateAwaitingReleaseGuard(gh);
  check('regressie: gewoon issue krijgt awaiting-release nog gewoon wel', { res, added: gh.calls.added },
        { res: 'set', added: ['status: awaiting-release'] });

  // ---------- label-awaiting-release.yml: strong-only selectie (#838-vervolg) ----------
  // De oorspronkelijke #838-fix loste alleen de epic-variant op. #820/#821/#822/#823
  // werden nadien alsnog ten onrechte op 'awaiting-release' gezet doordat een latere
  // PR-body ze in proza noemde (bijv. een cross-referentietabel of "zie #821 e.v.") —
  // niet-epic-issues waar helemaal nog niets aan gebouwd was. De workflow selecteert nu
  // `strong` in plaats van `all` vóór de labelloop; dit simuleert precies dat selectiegedrag.
  console.log('\nlabel-awaiting-release.yml strong-only selectie:');

  function simulateSelection(title, body) {
    const { strong } = extractIssueRefs(title, body);
    return [...strong].sort((a, b) => a - b);
  }

  check(
    'PR-body noemt #821 alleen in proza — niet geselecteerd voor labelen',
    simulateSelection('refactor(#819): iets', 'Gerelateerd aan #820 en #821 e.v.'),
    [819],
  );
  check(
    'PR met meerdere sluitende issues — allemaal geselecteerd',
    simulateSelection('chore: opruiming', 'Closes #820\nFixes #821'),
    [820, 821],
  );

  // ---------- close-released-issues.yml: epic-guard + merged_at-filter (#1179) ----------
  // Simuleert de twee beslissingen uit de sluitloop. Die workflow draait alleen op een
  // release-tag, dus zonder deze simulatie zou een fout er pas bij een echte release uitkomen —
  // en dan heeft hij al issues gesloten die met de hand heropend moeten worden.
  console.log('\nclose-released-issues.yml epic-guard + merged_at:');

  // De sluitloop: PR's overslaan, epics overslaan, de rest sluiten.
  function simulateSluiten(issue) {
    if (issue.pull_request) return 'overgeslagen: pr';
    if (isEpic(issue)) return 'overgeslagen: epic';
    return issue.state === 'open' ? 'gesloten' : 'al gesloten';
  }

  check('epic wordt niet gesloten door een release',
        simulateSluiten({ state: 'open', labels: [{ name: 'epic' }, { name: 'type: feature' }] }),
        'overgeslagen: epic');
  check('regressie: gewoon open issue wordt nog gewoon gesloten',
        simulateSluiten({ state: 'open', labels: [{ name: 'type: bug' }] }),
        'gesloten');
  check('PR-nummer in de lijst wordt overgeslagen',
        simulateSluiten({ state: 'open', pull_request: {}, labels: [] }),
        'overgeslagen: pr');

  // Bron 3: alleen GEMERGEDE PR's dragen nummers aan.
  function simulateBron3(prs) {
    const numbers = new Set();
    for (const pr of prs) {
      if (!pr.merged_at) continue;
      for (const n of extractIssueRefs(pr.title, pr.body).strong) numbers.add(n);
    }
    return [...numbers].sort((a, b) => a - b);
  }

  check('open PR op dezelfde commit draagt niets bij',
        simulateBron3([
          { title: 'fix(#100): gemerged werk', body: '', merged_at: '2026-01-01T00:00:00Z' },
          { title: 'fix(#999): nog open branch vanaf develop', body: '', merged_at: null },
        ]),
        [100]);

  // #1179-vervolg: het vangnet-rapport filtert op wat de sluitstap ECHT afhandelde, niet op de
  // ruwe kandidatenlijst. Zou het op die kandidatenlijst filteren, dan verdwijnt een overgeslagen
  // epic stilzwijgend uit het rapport terwijl hij nog op 'awaiting-release' staat — precies de
  // blinde vlek waarvoor #1168 is aangemaakt.
  console.log('\nclose-released-issues.yml vangnet-rapport:');

  // Sluitloop + rapport, samen gesimuleerd.
  function simulateRapport(kandidaten, nogGelabeld) {
    const afgehandeld = [];
    for (const issue of kandidaten) {
      if (issue.pull_request) continue;
      if (isEpic(issue)) continue;
      afgehandeld.push(issue.number);
    }
    const afgevinkt = new Set(afgehandeld);
    return nogGelabeld.filter(n => !afgevinkt.has(n)).sort((a, b) => a - b);
  }

  check(
    'overgeslagen epic blijft in het vangnet-rapport staan',
    simulateRapport(
      [{ number: 100, state: 'open', labels: [{ name: 'type: bug' }] },
       { number: 986, state: 'open', labels: [{ name: 'epic' }] }],
      [100, 986],
    ),
    [986],
  );
  check(
    'regressie: een echt afgehandeld issue verdwijnt wel uit het rapport',
    simulateRapport(
      [{ number: 100, state: 'open', labels: [{ name: 'type: bug' }] }],
      [100],
    ),
    [],
  );


  // #1669: een merge naar develop haalt elk turn:-label weg; status en andere labels blijven staan.
  gh = fakeGithub(['status: awaiting-release', 'turn: owner', 'type: bug']);
  let turns = await clearTurnLabels({ github: gh, context: ctx, core, issueNumber: 1 });
  check('turn: owner wordt na merge verwijderd', { turns, removed: gh.calls.removed }, { turns: ['turn: owner'], removed: ['turn: owner'] });

  gh = fakeGithub(['turn: codex', 'turn: claude-code', 'source: owner']);
  turns = await clearTurnLabels({ github: gh, context: ctx, core, issueNumber: 1 });
  check('meerdere turn-labels allemaal verwijderd, source blijft', { removed: gh.calls.removed }, { removed: ['turn: codex', 'turn: claude-code'] });

  gh = fakeGithub(['status: awaiting-release', 'type: bug']);
  turns = await clearTurnLabels({ github: gh, context: ctx, core, issueNumber: 1 });
  check('geen turn-label: niets te doen', { turns, removed: gh.calls.removed }, { turns: [], removed: [] });

  gh = fakeGithub(['turn: owner'], { isPr: true });
  turns = await clearTurnLabels({ github: gh, context: ctx, core, issueNumber: 1 });
  check('een PR wordt overgeslagen', { turns, removed: gh.calls.removed }, { turns: [], removed: [] });

  gh = fakeGithub(['turn: owner'], { throws: true });
  turns = await clearTurnLabels({ github: gh, context: ctx, core, issueNumber: 1 });
  check('ophaalfout: waarschuwing, geen crash', { turns, removed: gh.calls.removed }, { turns: [], removed: [] });


  // #1669 (Codex r1): een mislukte verwijdering mag niet als succes eindigen.
  function fakeGithubRemoveFout(labels, statusPerLabel) {
    const calls = { removed: [] };
    return {
      calls,
      rest: { issues: {
        get: async () => ({ data: { labels: labels.map(name => ({ name })) } }),
        removeLabel: async ({ name }) => {
          const st = statusPerLabel[name];
          if (st) { const e = new Error('fout ' + st); e.status = st; throw e; }
          calls.removed.push(name);
        },
      } },
    };
  }
  async function verwachtFout(naam, promise, verwachteTekst) {
    let msg = null;
    try { await promise; } catch (e) { msg = e.message; }
    check(naam, msg !== null && msg.includes(verwachteTekst), true);
  }
  gh = fakeGithubRemoveFout(['turn: owner'], { 'turn: owner': 500 });
  await verwachtFout('500-fout bij verwijderen: de helper faalt hard', clearTurnLabels({ github: gh, context: ctx, core, issueNumber: 1 }), 'turn: owner');

  gh = fakeGithubRemoveFout(['turn: owner', 'turn: codex'], { 'turn: owner': 500 });
  await verwachtFout('gedeeltelijke mislukking: faalt, maar het andere label is wel verwijderd', clearTurnLabels({ github: gh, context: ctx, core, issueNumber: 1 }), 'turn: owner');
  check('gedeeltelijke mislukking: overige labels toch geprobeerd', gh.calls.removed, ['turn: codex']);

  gh = fakeGithubRemoveFout(['turn: owner'], { 'turn: owner': 404 });
  turns = await clearTurnLabels({ github: gh, context: ctx, core, issueNumber: 1 });
  check('404 (al afwezig) is idempotent en geen fout', turns, ['turn: owner']);


  // ---------- #1867: fasecomment na merge ----------
  console.log('\nplaatsFasecommentGemerged (#1867):');
  function fakeComments(bestaand = []) {
    const calls = { created: [], listed: 0 };
    return {
      calls,
      paginate: async (fn, args) => { calls.listed++; return bestaand; },
      rest: { issues: {
        listComments: () => {},
        createComment: async ({ body, issue_number }) => { calls.created.push({ body, issue_number }); },
      } },
    };
  }
  const SHA = 'a'.repeat(40);
  gh = fakeComments();
  res = await plaatsFasecommentGemerged({ github: gh, context: ctx, core, issueNumber: 7, prNumber: 55, mergeSha: SHA });
  check('merge: fasecomment geplaatst', res, 'posted');
  check('comment bevat fase, PR en volledige merge-SHA',
    gh.calls.created.length === 1 && /Fase: gemerged\nPR: #55\nMerge-SHA: a{40}/.test(gh.calls.created[0].body), true);
  check('comment bevat de idempotentiemarker', gh.calls.created[0].body.includes(FASE_GEMERGED_MARKER(55)), true);

  // Regressie (review ronde 1, P2): het ÉCHT gegenereerde comment moet door de hele keten
  // (isOverdracht → verzamelOverdrachten → beoordeel) als nieuwste overdracht tellen, anders
  // blijft een oudere overdracht leidend en volgt een valse GEEN_TURN-melding.
  const { evalueerIssue, parseOverdracht, isOverdracht } = require('./beurt-invariant.js');
  const gegenereerd = gh.calls.created[0].body;
  const NU_MS = Date.parse('2026-11-14T12:00:00Z');
  const oudeReview = {
    body: `Fase: review\nImplementer: Claude Code\nReviewer: Codex\nPR: #55\nAangeboden head-SHA: ${'b'.repeat(40)}\nVolgende beurt: codex`,
    author_association: 'OWNER', created_at: '2026-11-14T09:00:00Z',
  };
  const botComment = (body) => ({ body, user: { type: 'Bot', login: 'github-actions[bot]' }, author_association: 'NONE', created_at: '2026-11-14T10:00:00Z' });
  const keten = (body, labels) => evalueerIssue({ now: NU_MS, graceMinuten: 30, prs: [], labels, issueComments: [oudeReview, botComment(body)] }).map(f => f.code);

  check('gegenereerde comment telt als overdracht', isOverdracht(parseOverdracht(gegenereerd)), true);
  check('eindfase wint over oudere reviewoverdracht (geen valse GEEN_TURN)', keten(gegenereerd, []), []);
  check('eindfase wint: achtergebleven turn-label wordt wél gesignaleerd', keten(gegenereerd, ['turn: codex']), ['TURN_BIJ_EINDE']);
  // Bewijs dat de test rood kan worden: het eerdere formaat zonder herkend veld werd genegeerd.
  const oudFormaat = gegenereerd.replace('Volgende beurt: geen\n', '');
  check('zonder herkend veld wordt de comment genegeerd (oud formaat)', isOverdracht(parseOverdracht(oudFormaat)), false);
  check('oud formaat: oudere overdracht blijft leidend (valse GEEN_TURN)', keten(oudFormaat, []), ['GEEN_TURN']);

  gh = fakeComments([{ body: `${FASE_GEMERGED_MARKER(55)}\nFase: gemerged` }]);
  res = await plaatsFasecommentGemerged({ github: gh, context: ctx, core, issueNumber: 7, prNumber: 55, mergeSha: SHA });
  check('herhaald event: geen tweede comment', { res, n: gh.calls.created.length }, { res: 'unchanged', n: 0 });

  gh = fakeComments([{ body: `${FASE_GEMERGED_MARKER(54)}\nFase: gemerged` }]);
  res = await plaatsFasecommentGemerged({ github: gh, context: ctx, core, issueNumber: 7, prNumber: 55, mergeSha: SHA });
  check('andere PR eerder gemerged: wel een nieuwe comment', { res, n: gh.calls.created.length }, { res: 'posted', n: 1 });

  gh = fakeComments();
  res = await plaatsFasecommentGemerged({ github: gh, context: ctx, core, issueNumber: 7, prNumber: 55, mergeSha: null });
  check('ontbrekende merge-SHA: niets geplaatst', { res, n: gh.calls.created.length }, { res: 'skipped', n: 0 });
  res = await plaatsFasecommentGemerged({ github: gh, context: ctx, core, issueNumber: 7, prNumber: 55, mergeSha: 'abc123' });
  check('afgekapte merge-SHA: niets geplaatst', { res, n: gh.calls.created.length }, { res: 'skipped', n: 0 });

  // ---------- #1867: merge versus sluiten zonder merge ----------
  console.log('\nstatusVoorPullRequest (#1867):');
  check('draft', statusVoorPullRequest({ state: 'open', draft: true }), 'status: in-progress');
  check('ready', statusVoorPullRequest({ state: 'open', draft: false }), 'status: review-needed');
  check('gemerged', statusVoorPullRequest({ state: 'closed', merged: true }), 'status: awaiting-release');
  check('gemerged via merged_at', statusVoorPullRequest({ state: 'closed', merged_at: '2026-01-01T00:00:00Z' }), 'status: awaiting-release');
  check('gesloten zonder merge: geen status', statusVoorPullRequest({ state: 'closed', merged: false }), null);
  check('ontbrekende PR: geen status', statusVoorPullRequest(null), null);

  console.log('\nsluiten zonder merge raakt awaiting-release niet:');
  const prSluit = (merged) => ({
    title: 'fix(#9): iets', body: '', draft: false, merged,
    user: { login: 'dev', type: 'User' },
    base: { repo: { id: 1 } }, head: { repo: { id: 1 } },
  });
  function fakeRepoGithub({ issueLabels = [], issueState = 'open', open = [], permission = 'write' }) {
    const calls = { added: [], removed: [], created: [] };
    return {
      calls,
      paginate: async () => open,
      request: async () => ({ data: { permission } }),
      rest: { issues: {
        get: async () => ({ data: { labels: issueLabels.map(name => ({ name })), state: issueState } }),
        addLabels: async ({ labels: l }) => { calls.added.push(...l); },
        removeLabel: async ({ name }) => { calls.removed.push(name); },
        createComment: async ({ body }) => { calls.created.push(body); },
      }, pulls: { list: () => {} } },
    };
  }
  const ctxPr = (action, pr) => ({ repo: { owner: 'o', repo: 'r' }, payload: { action, pull_request: pr, repository: { id: 1 } } });
  gh = fakeRepoGithub({ issueLabels: ['status: awaiting-release'] });
  res = await updatePullRequestIssueStatuses({ github: gh, context: ctxPr('closed', prSluit(false)), core });
  check('gesloten zonder merge: awaiting-release blijft (beschermd)', { added: gh.calls.added, removed: gh.calls.removed }, { added: [], removed: [] });
  gh = fakeRepoGithub({ issueLabels: ['status: review-needed'] });
  res = await updatePullRequestIssueStatuses({ github: gh, context: ctxPr('closed', prSluit(false)), core });
  check('gesloten zonder merge: terug naar triage', { res, added: gh.calls.added }, { res: 'updated', added: ['status: triage'] });
  gh = fakeRepoGithub({ issueLabels: ['status: review-needed'] });
  res = await updatePullRequestIssueStatuses({ github: gh, context: ctxPr('closed', prSluit(true)), core });
  check('gemerged: dit pad wijzigt niets (merge-workflow is eigenaar)', { res, added: gh.calls.added }, { res: 'merged', added: [] });


  // ---------- #1867: de echte workflow-code van label-awaiting-release.yml ----------
  // Voert het script-blok uit het YAML-bestand zelf uit (geen nagebootste beslislogica), zodat een
  // wijziging in de workflow door deze test wordt gezien.
  console.log('\nlabel-awaiting-release.yml (echte scriptblok):');
  const fs = require('fs');
  const path = require('path');
  const repoRoot = path.resolve(__dirname, '..', '..');
  function laadWorkflowScript() {
    const regels = fs.readFileSync(path.join(repoRoot, '.github/workflows/label-awaiting-release.yml'), 'utf8').split('\n');
    const i = regels.findIndex(r => /^\s+script: \|\s*$/.test(r));
    const inspringing = regels[i + 1].match(/^ */)[0].length;
    const blok = [];
    for (let j = i + 1; j < regels.length; j++) {
      if (regels[j].trim() !== '' && regels[j].match(/^ */)[0].length < inspringing) break;
      blok.push(regels[j].slice(inspringing));
    }
    const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
    return new AsyncFunction('github', 'context', 'core', 'require', blok.join('\n'));
  }
  async function draaiWorkflow({ mergeSha, bestaandeComments = [], labels = ['status: in-progress', 'turn: codex'] }) {
    process.env.GITHUB_WORKSPACE = repoRoot;
    const out = { failed: [], created: [], added: [], removed: [] };
    const github = {
      paginate: async () => bestaandeComments,
      rest: { issues: {
        get: async () => ({ data: { labels: labels.map(name => ({ name })), state: 'open' } }),
        addLabels: async ({ labels: l }) => { out.added.push(...l); },
        removeLabel: async ({ name }) => { out.removed.push(name); },
        createComment: async ({ body }) => { out.created.push(body); },
        update: async () => {},
        listComments: () => {},
      } },
    };
    const context = {
      repo: { owner: 'o', repo: 'r' },
      payload: { pull_request: { number: 77, title: 'fix(#9): iets', body: '', merge_commit_sha: mergeSha } },
    };
    const wcore = { notice: () => {}, warning: () => {}, setFailed: (m) => { out.failed.push(m); } };
    await laadWorkflowScript()(github, context, wcore, require);
    return out;
  }

  let wf = await draaiWorkflow({ mergeSha: 'c'.repeat(40) });
  check('geldige merge-SHA: één fasecomment, geen fout', { n: wf.created.length, failed: wf.failed }, { n: 1, failed: [] });
  check('geldige merge-SHA: status gezet en turn opgeschoond', { added: wf.added, removed: wf.removed },
    { added: ['status: awaiting-release'], removed: ['status: in-progress', 'turn: codex'] });

  for (const [naam, sha] of [['ontbrekende', null], ['lege', ''], ['afgekapte', 'abc123'], ['niet-hexadecimale', 'z'.repeat(40)]]) {
    wf = await draaiWorkflow({ mergeSha: sha });
    check(`${naam} merge-SHA: workflow faalt zichtbaar (core.setFailed)`, { n: wf.failed.length, created: wf.created.length }, { n: 1, created: 0 });
  }

  wf = await draaiWorkflow({ mergeSha: 'c'.repeat(40), bestaandeComments: [{ body: `${FASE_GEMERGED_MARKER(77)}\nFase: gemerged` }] });
  check('herhaalde run met bestaande marker: geen tweede comment en geen fout', { created: wf.created.length, failed: wf.failed }, { created: 0, failed: [] });

  // ---------- #1867: reconciliatie ----------
  console.log('\nreconcileIssueStatus (#1867):');
  const prOpen = (nr, draft, extra = {}) => ({
    number: nr, title: `fix(#9): werk ${nr}`, body: '', draft, state: 'open',
    user: { login: 'dev', type: 'User' }, base: { repo: { id: 1 } }, head: { repo: { id: 1 } }, ...extra,
  });
  const ctxRec = { repo: { owner: 'o', repo: 'r' }, payload: { repository: { id: 1 } } };
  const rec = (g, nr = 9) => reconcileIssueStatus({ github: g, context: ctxRec, core, issueNumber: nr });

  gh = fakeRepoGithub({ issueLabels: ['status: review-needed', 'turn: owner'], open: [prOpen(100, true)] });
  res = await rec(gh);
  check('draft-PR: review-needed hersteld naar in-progress', { res, added: gh.calls.added, removed: gh.calls.removed },
    { res: 'set', added: ['status: in-progress'], removed: ['status: review-needed'] });
  check('turn-label wordt nooit aangeraakt', gh.calls.removed.includes('turn: owner') || gh.calls.added.some(l => l.startsWith('turn:')), false);

  gh = fakeRepoGithub({ issueLabels: ['status: in-progress'], open: [prOpen(100, false)] });
  res = await rec(gh);
  check('ready-PR: in-progress hersteld naar review-needed', { res, added: gh.calls.added }, { res: 'set', added: ['status: review-needed'] });

  gh = fakeRepoGithub({ issueLabels: ['status: in-progress'], open: [prOpen(100, true)] });
  res = await rec(gh);
  check('al juist: idempotent', { res, added: gh.calls.added, removed: gh.calls.removed }, { res: 'unchanged', added: [], removed: [] });
  res = await rec(gh);
  check('tweede run: nog steeds idempotent', res, 'unchanged');

  gh = fakeRepoGithub({ issueLabels: ['status: review-needed'], open: [] });
  res = await rec(gh);
  check('geen gekoppelde PR: niets gewijzigd', { res, added: gh.calls.added, removed: gh.calls.removed }, { res: 'unlinked', added: [], removed: [] });

  gh = fakeRepoGithub({ issueLabels: ['status: review-needed'], open: [prOpen(100, true, { title: 'chore: zie #9 in proza', body: 'verwijst naar #9' })] });
  res = await rec(gh);
  check('alleen zwakke verwijzing telt niet als koppeling', { res, added: gh.calls.added }, { res: 'unlinked', added: [] });

  gh = fakeRepoGithub({ issueLabels: ['status: review-needed'], open: [prOpen(100, true), prOpen(101, false)] });
  res = await rec(gh);
  check('meerdere sterke koppelingen: niets gewijzigd', { res, added: gh.calls.added, removed: gh.calls.removed }, { res: 'ambiguous', added: [], removed: [] });

  for (const beschermd of ['status: blocked', 'status: waiting-owner', 'status: wont-fix', 'status: awaiting-release']) {
    gh = fakeRepoGithub({ issueLabels: [beschermd], open: [prOpen(100, true)] });
    res = await rec(gh);
    check(`${beschermd} blijft beschermd`, { res, added: gh.calls.added, removed: gh.calls.removed }, { res: 'protected', added: [], removed: [] });
  }

  gh = fakeRepoGithub({ issueLabels: ['status: review-needed'], issueState: 'closed', open: [prOpen(100, true)] });
  res = await rec(gh);
  check('gesloten issue: niets gewijzigd', { res, added: gh.calls.added }, { res: 'closed', added: [] });

  gh = fakeRepoGithub({ issueLabels: ['status: review-needed'], open: [prOpen(100, true, { head: { repo: { id: 2 } } })] });
  res = await rec(gh);
  check('fork-PR (onbewezen herkomst): niets gewijzigd', { res, added: gh.calls.added }, { res: 'untrusted', added: [] });

  gh = fakeRepoGithub({ issueLabels: ['status: review-needed'], open: [prOpen(100, true)], permission: 'read' });
  res = await rec(gh);
  check('auteur zonder schrijfrechten: niets gewijzigd', { res, added: gh.calls.added }, { res: 'untrusted', added: [] });

  console.log(failures === 0 ? '\nALLE TESTS GESLAAGD' : `\n${failures} TEST(S) GEFAALD`);
  process.exit(failures === 0 ? 0 : 1);
}
run();
