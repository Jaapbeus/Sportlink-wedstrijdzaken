// Unit-tests voor issue-status.js — draaien zonder GitHub, zonder token, zonder netwerk.
// Uitvoeren: node .github/scripts/issue-status.test.js   (exit 0 = alles groen)
// Wordt bij elke PR gedraaid door de job 'Build FunctionApp + BlazorAdmin' in build.yml.
const { extractIssueRefs, setIssueStatus, clearIssueStatus, isEpic } = require('./issue-status.js');

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

  console.log(failures === 0 ? '\nALLE TESTS GESLAAGD' : `\n${failures} TEST(S) GEFAALD`);
  process.exit(failures === 0 ? 0 : 1);
}
run();
