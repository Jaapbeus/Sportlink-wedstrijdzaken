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

  console.log(failures === 0 ? '\nALLE TESTS GESLAAGD' : `\n${failures} TEST(S) GEFAALD`);
  process.exit(failures === 0 ? 0 : 1);
}
run();
